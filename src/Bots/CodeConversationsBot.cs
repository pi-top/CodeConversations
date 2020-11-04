// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Server;
using Newtonsoft.Json.Linq;
using CodeConversations.Infrastructure;
using CodeConversations.Models;
using CodeConversations.Workers;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.Extensions.Configuration;

namespace CodeConversations.Bots
{
    public class CodeConversationsBot : TeamsActivityHandler
    {
        private UserState _userState;

        readonly string regularExpression = @"(\r(.*?)\r)";

        private readonly IConfiguration _configuration;
        private string _botId;

        public CodeConversationsBot(UserState userState,
            IConfiguration configuration)
        {
            _configuration = configuration;
            _userState = userState;
            _botId = configuration["MicrosoftAppId"];
        }


        public override async Task OnTurnAsync(ITurnContext turnContext,
            CancellationToken cancellationToken)
        {
            await base.OnTurnAsync(turnContext, cancellationToken);
            await _userState.SaveChangesAsync(turnContext, false, cancellationToken);
        }

        protected override async Task OnTeamsMembersAddedAsync(IList<TeamsChannelAccount> teamsMembersAdded,
            TeamInfo teamInfo,
            ITurnContext<IConversationUpdateActivity> turnContext,
            CancellationToken cancellationToken)
        {
            await base.OnTeamsMembersAddedAsync(teamsMembersAdded, teamInfo, turnContext, cancellationToken);
            DotNetInteractiveProcessRunner.Instance.SessionLanguage = "csharp";
            var card = CardUtilities.CreateAdaptiveCardAttachment(CardJsonFiles.IntroduceRover);
            var attach = MessageFactory.Attachment(card);
            await turnContext.SendActivityAsync(attach);
        }

#pragma warning disable CS1998
        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext,
            CancellationToken cancellationToken)
        {
            var value = turnContext.Activity;
            var attachments = turnContext.Activity.Attachments;
            if (turnContext.Activity.Value == null) // someone typed in something, it isn't a card
            {

                var content = turnContext.Activity.Text;
                var code = CheckForCode(content);

                var conversationReference = turnContext.Activity.GetConversationReference();
                var mention = new Mention
                {
                    Mentioned = turnContext.Activity.From,
                    Text = $"<at>{turnContext.Activity.From.Name}</at>",
                };

                if (!string.IsNullOrEmpty(code))
                {
                    if (DotNetInteractiveProcessRunner.Instance.CanExecuteCode)
                    {
                        var submissionToken = Guid.NewGuid().ToString("N");
                        var messageText = string.Empty;
                        var user = UserGame.GetOrCreateUser(mention, turnContext.Activity.From);
                        if (UserGame.CurrentChatUser?.Id != user.Id)
                        {
                            UserGame.CurrentChatUser = user;
                            messageText = $"Hey {mention.Text}, I see that you have written some code!\r\n I got: \r\n```{code}```\r\n Let me run that for you! 😊";
                        }
                        else
                        {
                            messageText = UserGame.GetMessageForUser( mention);
                        }

                        await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                        {
                            var message = MessageFactory.Text(messageText);
                            if (messageText.Contains(mention.Text))
                            {
                                message.Entities.Add(mention);
                            }
                            await context.SendActivityAsync(message, token);
                        }, cancellationToken);

                        // build the envelope
                        var submitCode = new SubmitCode(code);
                        submitCode.SetToken(submissionToken);
                        var envelope = KernelCommandEnvelope.Create(submitCode);
                        var channel = ContentSubjectHelper.GetOrCreateChannel(submissionToken);
                        EnvelopeHelper.StoreEnvelope(submissionToken, envelope);
                        var cardSent = false;
                        channel
                            .Timeout(DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(1)))
                            .Buffer(TimeSpan.FromSeconds(1))
                            .Subscribe(
                         onNext: async formattedValues =>
                         {
                             turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference,
                                 (context, token) =>
                                 {
                                     if (formattedValues.Count > 0)
                                     {
                                         var hasHtml = formattedValues.Any(f => f.MimeType == HtmlFormatter.MimeType);
                                         if (hasHtml && formattedValues.Count == 1 && IsFormattedValueImage(formattedValues.ElementAt(0)))
                                         {
                                           string src = GetSrcFromImageHtml(formattedValues.ElementAt(0).Value);
                                           var attachment = new Attachment
                                           {
                                             ContentType = "image/*",
                                             ContentUrl = src,
                                           };
                                           var message = MessageFactory.Attachment(attachment);
                                           context.SendActivityAsync(message, token).Wait();
                                         }
                                         else if (hasHtml && formattedValues.Count == 1 && IsFormattedValueClassification(formattedValues.ElementAt(0)))
                                         {
                                           var info = GetInfoFromClassificationHtml(formattedValues.ElementAt(0).Value);
                                           var content = $"**Label**: _{info[0]}_\r\n\n**Confidence**: _{info[1]}_";
                                           var message = MessageFactory.Text(content);
                                           context.SendActivityAsync(message, token).Wait();
                                         }
                                         else if (hasHtml)
                                         {
                                             if (!cardSent)
                                             {
                                                 cardSent = true;
                                                 var card = new HeroCard
                                                 {
                                                     Title = "Your output is too awesome 😎",
                                                     Subtitle = "Use the viewer to see it.",
                                                     Buttons = new List<CardAction>
                                                     {
                                                        new TaskModuleAction("Open Viewer",
                                                            new {data = submissionToken})
                                                     },
                                                 }.ToAttachment();
                                                 var message = MessageFactory.Attachment(card);
                                                 context.SendActivityAsync(message, token).Wait();
                                             }
                                         }
                                         else
                                         {
                                             var content = string.Join("\r\n", formattedValues.Select(f => f.Value));
                                             var message = MessageFactory.Text($"```\r\n{content}");
                                             context.SendActivityAsync(message, token).Wait();
                                         }
                                     }

                                     return Task.CompletedTask;
                                 }, cancellationToken).Wait();
                         }, onCompleted: async () =>
                         {
                             await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                             {
                                 await Task.Delay(1000);
                                 var message = MessageFactory.Text($"Good news, {mention.Text}! I'm all done here 👍");
                                 message.Entities.Add(mention);
                                 await context.SendActivityAsync(message, token);
                             }, cancellationToken);
                         },
                           onError: async error =>
                           {
                               await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                               {
                                   await Task.Delay(1000);
                                   var message = MessageFactory.Text($"Hate to break this to you {mention.Text}, but there were some issues... 👎\r\n```{error.Message}```");
                                   message.Entities.Add(mention);
                                   await context.SendActivityAsync(message, token);
                               }, cancellationToken);
                           });

                        user.IncrementCodeSubmissionCount();
                        await DotNetInteractiveProcessRunner.Instance.ExecuteEnvelope(submissionToken);
                    }
                    else
                    {
                        await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                        {
                            var message = MessageFactory.Text($"Sorry {mention.Text}, but I cannot execute your code right now. 😓");
                            message.Entities.Add(mention);
                            await context.SendActivityAsync(message, token);
                        }, cancellationToken);
                    }
                }
                else if (string.IsNullOrWhiteSpace(DotNetInteractiveProcessRunner.Instance.SessionLanguage))
                {
                    var card = CardUtilities.CreateAdaptiveCardAttachment(CardJsonFiles.SelectLanguage);
                    var attach = MessageFactory.Attachment(card);
                    await turnContext.SendActivityAsync(attach, cancellationToken);
                }
                else if (content.Contains("👊"))
                {
                    var mentioned = turnContext.Activity.GetMentions()?.FirstOrDefault(m => m.Mentioned.Id.EndsWith(_botId));
                    if (mentioned != null)
                    {
                        await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference,
                            async (context, token) =>
                            {
                                var message = MessageFactory.Text($"Right back at ya, {mention.Text}! 👊");
                                message.Entities.Add(mention);
                                await context.SendActivityAsync(message, token);
                            }, cancellationToken);
                    }
                }
                else if (content.Contains("Hello"))
                {
                    var mentioned = turnContext.Activity.GetMentions()?.FirstOrDefault(m => m.Mentioned.Id.EndsWith(_botId));
                    if (mentioned != null)
                    {
                        var card = CardUtilities.CreateAdaptiveCardAttachment(CardJsonFiles.IntroduceRover);
                        var attach = MessageFactory.Attachment(card);
                        await turnContext.SendActivityAsync(attach, cancellationToken);
                    }
                }
                else if (content.Contains("help"))
                {

                    var helpTopic = GetHelpTopic(content);

                    var helpCode = $@"
                    using System.ComponentModel;
                    using System;
                    using System.Reflection;
                    Console.WriteLine(""Properties: "");
                    foreach(PropertyDescriptor descriptor in TypeDescriptor.GetProperties({helpTopic}))
                    {{
                            string name=descriptor.Name;
                            // object value=descriptor.GetValue({helpTopic});
                            Console.WriteLine(""  {{0}}"",name);
                    }}
                    Console.WriteLine(""Methods: "");
                    foreach(MethodInfo method in {helpTopic}.GetType().GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public))
                    {{
                            if (!char.IsLower(method.Name[0])) {{
                                string name=method.Name;
                                Console.WriteLine(""  {{0}}"",name);
                            }}
                    }}
                    ";

                    var submissionToken = Guid.NewGuid().ToString("N");
                    var submitCode = new SubmitCode(helpCode);

                    submitCode.SetToken(submissionToken);
                    var envelope = KernelCommandEnvelope.Create(submitCode);
                    var channel = ContentSubjectHelper.GetOrCreateChannel(submissionToken);
                    EnvelopeHelper.StoreEnvelope(submissionToken, envelope);
                    var cardSent = false;

                    channel
                        .Timeout(DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(1)))
                        .Buffer(TimeSpan.FromSeconds(1))
                        .Subscribe(
                           onNext: async formattedValues =>
                           {
                               turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference,
                                   (context, token) =>
                                   {
                                       if (formattedValues.Count > 0)
                                       {
                                           var hasHtml = formattedValues.Any(f => f.MimeType == HtmlFormatter.MimeType);
                                           if (hasHtml)
                                           {
                                               if (!cardSent)
                                               {
                                                   cardSent = true;
                                                   var card = new HeroCard
                                                   {
                                                       Title = "Your output is too awesome 😎",
                                                       Subtitle = "Use the viewer to see it.",
                                                       Buttons = new List<CardAction>
                                                       {
                                                          new TaskModuleAction("Open Viewer",
                                                              new {data = submissionToken})
                                                       },
                                                   }.ToAttachment();
                                                   var message = MessageFactory.Attachment(card);
                                                   context.SendActivityAsync(message, token).Wait();
                                               }
                                           }
                                           else
                                           {
                                               var content = string.Join("\r\n", formattedValues.Select(f => f.Value));
                                               var message = MessageFactory.Text($"```\r\n{content}");
                                               context.SendActivityAsync(message, token).Wait();
                                           }
                                       }

                                       return Task.CompletedTask;
                                   }, cancellationToken).Wait();
                           }, onCompleted: async () =>
                           {
                               await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                               {
                                   await Task.Delay(1000);
                                   var message = MessageFactory.Text($"So, {mention.Text}, anything there look interesting to you?");
                                   message.Entities.Add(mention);
                                   await context.SendActivityAsync(message, token);
                               }, cancellationToken);
                           },
                           onError: async error =>
                           {
                               await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                               {
                                   await Task.Delay(1000);
                                   var message = MessageFactory.Text($"Hate to break this to you {mention.Text}, but there were some issues... 👎\r\n```{error.Message}```");
                                   message.Entities.Add(mention);
                                   await context.SendActivityAsync(message, token);
                               }, cancellationToken);
                           });

                        await DotNetInteractiveProcessRunner.Instance.ExecuteEnvelope(submissionToken);
                }
            }
            else
            {
                var userAction = turnContext.Activity.Value;

                if (((JObject)userAction).Value<string>("userAction").Equals("SelectLanguage"))
                {
                    if (string.IsNullOrWhiteSpace(DotNetInteractiveProcessRunner.Instance.SessionLanguage))
                    {
                        var language = ((JObject)userAction).Value<string>("language");
                        DotNetInteractiveProcessRunner.Instance.SessionLanguage = language;
                        var languageLabel = ((JObject)userAction).Value<string>("languageLabel");
                        var message = MessageFactory.Text($"All set. Let's write some {DotNetInteractiveProcessRunner.Instance.SessionLanguage} code together! 🤘🏻");
                        await turnContext.SendActivityAsync(message, cancellationToken);
                    }
                }
                else if (((JObject)userAction).Value<string>("userAction").Equals("BlinkLights"))
                {
                    if (DotNetInteractiveProcessRunner.Instance.CanExecuteCode)
                    {
                        var message = MessageFactory.Text("Blinking 💡...");
                        await turnContext.SendActivityAsync(message, cancellationToken);

                        var conversationReference = turnContext.Activity.GetConversationReference();
                        var submissionToken = Guid.NewGuid().ToString("N");
                        var submitCode = new SubmitCode("roverBody.BlinkAllLights();");

                        submitCode.SetToken(submissionToken);
                        var envelope = KernelCommandEnvelope.Create(submitCode);
                        var channel = ContentSubjectHelper.GetOrCreateChannel(submissionToken);
                        EnvelopeHelper.StoreEnvelope(submissionToken, envelope);

                        channel
                            .Timeout(DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(3)))
                            .Buffer(TimeSpan.FromSeconds(1))
                            .Subscribe(
                               onNext: async formattedValues =>
                               {
                               },
                               onCompleted: async () =>
                               {
                                   await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                                   {
                                       await Task.Delay(2000);
                                       var message = MessageFactory.Text($"Don't I look great! Ok, now let's write some C# code and show off what we can achieve together! 🤘🏻");
                                       await context.SendActivityAsync(message, token);
                                   }, cancellationToken);
                               },
                               onError: async error =>
                               {
                                   await turnContext.Adapter.ContinueConversationAsync(_botId, conversationReference, async (context, token) =>
                                   {
                                       await Task.Delay(1000);
                                       var message = MessageFactory.Text($"Hmm, having trouble blinking my lights... 👎\r\n```{error.Message}```");
                                       await context.SendActivityAsync(message, token);
                                   }, cancellationToken);
                               });

                        await DotNetInteractiveProcessRunner.Instance.ExecuteEnvelope(submissionToken);
                    }
                }
            }
        }

        protected override async Task<TaskModuleResponse> OnTeamsTaskModuleFetchAsync(
            ITurnContext<IInvokeActivity> turnContext,
            TaskModuleRequest taskModuleRequest,
            CancellationToken cancellationToken)
        {

            var token = ((JObject)taskModuleRequest.Data)["data"].Value<string>();

            var url = $"https://{ _configuration["CodeConversationsDomain"]}/executor?Token={token}";

            return new TaskModuleResponse
            {
                Task = new TaskModuleContinueResponse
                {
                    Type = "continue",
                    Value = new TaskModuleTaskInfo
                    {
                        Height = "large",
                        Width = "large",
                        Title = "Powered by .NET interactive aka.ms/codeconversations",
                        Url = url,
                        FallbackUrl = url
                    }
                }
            };
        }
#pragma warning restore CS1998

        string CheckForCode(string content)
        {
            var isMatch = Regex.IsMatch(content, regularExpression);
            var code = string.Empty;
            if (DoesMessageContainCode(content))
            {
                code = GetCodeFromMessage(content);
                code = HttpUtility.HtmlDecode(code);
            }
            return code;
        }

        private bool DoesMessageContainCode(string messageText)
        {
            var matches = Regex.Matches(messageText, regularExpression, RegexOptions.Singleline);
            return matches.Any();
        }

        private string GetCodeFromMessage(string messageText)
        {
            var matches = Regex.Matches(messageText, regularExpression, RegexOptions.Singleline);
            var result = matches.First().Groups[2].Value;
            return result.Replace("\u200B", "");
        }

        private bool IsFormattedValueImage(FormattedValue formattedValue)
        {
            string imageRegex = @"^<img";
            if (!(formattedValue.Value is string))
            {
                return false;
            }
            var matches = Regex.Matches(formattedValue.Value, imageRegex, RegexOptions.Singleline);
            return matches.Any();
        }

        private string GetSrcFromImageHtml(string imageHtml)
        {
            string srcRegex = "(src=\"(.*?)\")";
            var matches = Regex.Matches(imageHtml, srcRegex, RegexOptions.Singleline);
            var result = matches.First().Groups[2].Value;
            Console.WriteLine(result);
            return result;
        }

        private bool IsFormattedValueClassification(FormattedValue formattedValue)
        {
            string classRegex = @"<tr><th>Label</th><th>Confidence</th></tr>";
            if (!(formattedValue.Value is string))
            {
                return false;
            }
            var matches = Regex.Matches(formattedValue.Value, classRegex, RegexOptions.Singleline);
            return matches.Any();
        }

        private string[] GetInfoFromClassificationHtml(string classHtml)
        {
            string valueRegex = "(dni-plaintext\">(.*?)<)";
            var matches = Regex.Matches(classHtml, valueRegex, RegexOptions.Singleline);
            var label = matches.First().Groups[2].Value;
            var confidence = matches.ElementAt(1).Groups[2].Value;
            string[] result = {label, confidence};
            return result;
        }

        private string GetHelpTopic(string content)
        {
            string topicRegex = "(help (.*?)$)";
            var matches = Regex.Matches(content, topicRegex, RegexOptions.Singleline);
            var result = matches.First().Groups[2].Value;
            return result;
        }
    }
}
