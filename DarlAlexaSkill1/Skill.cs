using Alexa.NET;
using Alexa.NET.LocaleSpeech;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DarlAlexaSkill1.Extensions;
using GraphQL.Client;
using Newtonsoft.Json.Converters;
using GraphQL.Common.Request;
using DarlAlexaSkill1.Models;
using Microsoft.Extensions.Configuration;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DarlAlexaSkill1
{
    public class Skill
    {
        GraphQLClient client = null;
        private static IConfiguration _config;

        readonly string qsText = "questionSet";

        public Skill(IConfiguration config)
        {
            _config = config;
            client = new GraphQLClient(_config["DarlAPIAddress"]);
            var authcode = _config["DarlAPIKey"];
            client.DefaultRequestHeaders.Add("Authorization", $"Basic {authcode}");
            client.Options.JsonSerializerSettings.Converters.Add(new StringEnumConverter());
        }


        [FunctionName("DarlAlexaSkill1")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "DarlAlexaSkill1/{file}")] HttpRequest req, string file, ILogger log)
        {
            var json = await req.ReadAsStringAsync();
            var skillRequest = JsonConvert.DeserializeObject<SkillRequest>(json);

            // Verifies that the request is indeed coming from Alexa.
            var isValid = await skillRequest.ValidateRequestAsync(req, log);
            if (!isValid)
            {
                return new BadRequestResult();
            }

            // Setup language resources.
            var store = SetupLanguageResources();
            var locale = skillRequest.CreateLocale(store);

            var request = skillRequest.Request;
            SkillResponse response = null;

            try
            {
                if (request is LaunchRequest launchRequest)
                {
                    log.LogInformation("Session started");
                    string filename = string.Empty;
                    if(string.IsNullOrEmpty(file))
                    {
                        filename = _config["DarlRuleSet"];
                    }
                    else
                    {
                        filename = file;
                    }
                    //
                    var glReq = new GraphQLRequest() {
                        Variables = new { ruleset = filename },
                        Query = @"query beginForm($ruleset: String!){ beginQuestionnaire(ruleSetName: $ruleset){ ieToken questionHeader percentComplete canUnwind preamble responseHeader questions { text categories reference qType maxval minval format dResponse sResponse } values {name value}}}" 
                    };
                    var resp = await client.PostAsync(glReq);
                    var qResp = resp.GetDataFieldAs<QuestionSet>("beginQuestionnaire");
                    //build composite message
                    response = ResponseBuilder.Ask($"{qResp.preamble}\n{qResp.questionHeader}\n{qResp.questions[0].text}", RepromptBuilder.Create(qResp.questions[0].text));
                    if (response.SessionAttributes == null)
                        response.SessionAttributes = new Dictionary<string, object>();
                    response.SessionAttributes.Add("questionSet", qResp);
                }
                else if (request is IntentRequest intentRequest)
                {
                    // Checks whether to handle system messages defined by Amazon.
                    var systemIntentResponse = await HandleSystemIntentsAsync(intentRequest, locale);
                    if (systemIntentResponse.IsHandled)
                    {
                        response = systemIntentResponse.Response;
                    }
                    else
                    {
                        // Processes request according to intentRequest.Intent.Name...
                        if(skillRequest.Session.Attributes.ContainsKey(qsText))
                        {
                            var qs = ((JObject)skillRequest.Session.Attributes[qsText]).ToObject<QuestionSet>();
                            var quest = qs.questions[0];
                            if(qs.questions != null && qs.questions.Count > 0)
                            {
                                switch(quest.qtype)
                                {
                                    case Question.QType.numeric: 
                                        quest.dResponse = Double.Parse(intentRequest.Intent.Slots["AMAZON.NUMBER"].Value);
                                        break;
                                    case Question.QType.categorical: 
                                        if(intentRequest.Intent.Name == "AMAZON.YesIntent")
                                        {
                                            //look for a match for "true", "Yes" in categories
                                            foreach(var c in quest.categories)
                                            {
                                                if(c.Equals("true", StringComparison.InvariantCultureIgnoreCase) ||
                                                    c.Equals("yes", StringComparison.InvariantCultureIgnoreCase))
                                                {
                                                    quest.sResponse = c;
                                                    break;
                                                }
                                            }
                                        }
                                        else if(intentRequest.Intent.Name == "AMAZON.NoIntent")
                                        {
                                            foreach (var c in quest.categories)
                                            {
                                                if (c.Equals("false", StringComparison.InvariantCultureIgnoreCase) ||
                                                    c.Equals("no", StringComparison.InvariantCultureIgnoreCase))
                                                {
                                                    quest.sResponse = c;
                                                    break;
                                                }
                                            }

                                        }
                                        else
                                        {
                                            //handle wrong intent...
                                        }
                                        break;
                                    case Question.QType.textual: //textual
                                        break;
                                    case Question.QType.temporal: //temporal
                                        quest.sResponse = intentRequest.Intent.Slots["AMAZON.DATE"].Value;
                                        break;
                                }

                            }
                            else if(qs.responses != null && qs.responses.Count > 0)
                            {
                                //could only get here if beginQuestionnaire returns responses immediately, i.e. no questions required.
                                //return tell - terminates conversation
                                var sb = new StringBuilder();
                                sb.AppendLine(qs.responseHeader);
                                foreach (var r in qs.responses)
                                {
                                    sb.AppendLine(r.annotation);
                                    sb.AppendLine(r.mainText);
                                }
                                response = ResponseBuilder.Tell(sb.ToString());
                            }
                            //pass QuestionSet back to graphQL

                            var glReq = new GraphQLRequest()
                            {
                                Variables = new { ieToken = qs.ieToken, reference = quest.reference, qType = quest.qtype, sresponse = quest.sResponse, dresponse = quest.dResponse },
                                Query = @"query nextStep($ieToken: String!, $reference: String!, $qType: QuestionType!, $sresponse: String, $dresponse: Float){ continueQuestionnaire(responses: { ieToken: $ieToken, questions:[{ reference: $reference, sResponse: $sresponse,dResponse: $dresponse, qType: $qType}]}) { complete ieToken questionHeader percentComplete canUnwind preamble responseHeader values { name value } questions { text categories  reference qType sResponse dResponse} responses { mainText annotation rType preamble}}}"
                            };
                            var resp = await client.PostAsync(glReq);
                            var qResp = resp.GetDataFieldAs<QuestionSet>("continueQuestionnaire");

                            if (qResp.questions != null && qResp.questions.Count > 0)
                            {
                                response = ResponseBuilder.Ask(qResp.questions[0].text, RepromptBuilder.Create(qResp.questions[0].text));
                                if (response.SessionAttributes == null)
                                    response.SessionAttributes = new Dictionary<string, object>();
                                response.SessionAttributes.Add("questionSet", qResp);
                            }
                            else
                            {//the questionnaire has terminated
                                if (qResp.responses != null && qResp.responses.Count > 0)
                                {
                                    var sb = new StringBuilder();
                                    sb.AppendLine(qs.responseHeader);
                                    foreach(var r in qResp.responses)
                                    {
                                        sb.AppendLine(r.annotation);
                                        sb.AppendLine(r.mainText);
                                    }
                                    response = ResponseBuilder.Tell(sb.ToString());                              
                                }

                            }

                        }
                    }
                }
                else if (request is SessionEndedRequest sessionEndedRequest)
                {
                    log.LogInformation("Session ended");
                    response = ResponseBuilder.Empty();
                }
            }
            catch(Exception ex)
            {
                var message = await locale.Get(LanguageKeys.Error, null);
                response = ResponseBuilder.Tell(message);
                response.Response.ShouldEndSession = false;
            }

            return new OkObjectResult(response);
        }

        private static async Task<(bool IsHandled, SkillResponse Response)> HandleSystemIntentsAsync(IntentRequest request, ILocaleSpeech locale)
        {
            SkillResponse response = null;

            if (request.Intent.Name == BuiltInIntent.Cancel)
            {
                var message = await locale.Get(LanguageKeys.Cancel, null);
                response = ResponseBuilder.Tell(message);
            }
            else if (request.Intent.Name == BuiltInIntent.Help)
            {
                var message = await locale.Get(LanguageKeys.Help, null);
                response = ResponseBuilder.Ask(message, RepromptBuilder.Create(message));
            }
            else if (request.Intent.Name == BuiltInIntent.Stop)
            {
                var message = await locale.Get(LanguageKeys.Stop, null);
                response = ResponseBuilder.Tell(message);
            }

            return (response != null, response);
        }

        private static DictionaryLocaleSpeechStore SetupLanguageResources()
        {
            // Creates the locale speech store for each supported languages.
            var store = new DictionaryLocaleSpeechStore();

            store.AddLanguage("en", new Dictionary<string, object>
            {
                [LanguageKeys.Welcome] = "Welcome to the skill!",
                [LanguageKeys.WelcomeReprompt] = "You can ask help if you need instructions on how to interact with the skill",
                [LanguageKeys.Response] = "This is just a sample answer",
                [LanguageKeys.Cancel] = "Canceling...",
                [LanguageKeys.Help] = "Help...",
                [LanguageKeys.Stop] = "Bye bye!",
                [LanguageKeys.Error] = "I'm sorry, there was an unexpected error. Please, try again later."
            });

            store.AddLanguage("it", new Dictionary<string, object>
            {
                [LanguageKeys.Welcome] = "Benvenuto nella skill!",
                [LanguageKeys.WelcomeReprompt] = "Se vuoi informazioni sulle mie funzionalità, prova a chiedermi aiuto",
                [LanguageKeys.Response] = "Questa è solo una risposta di prova",
                [LanguageKeys.Cancel] = "Sto annullando...",
                [LanguageKeys.Help] = "Aiuto...",
                [LanguageKeys.Stop] = "A presto!",
                [LanguageKeys.Error] = "Mi dispiace, si è verificato un errore imprevisto. Per favore, riprova di nuovo in seguito."
            });

            return store;
        }
    }
}
