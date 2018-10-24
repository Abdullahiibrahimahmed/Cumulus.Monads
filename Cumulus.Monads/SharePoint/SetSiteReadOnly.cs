using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Threading.Tasks;
using System.Web.Http.Description;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.SharePoint.Client;
using Cumulus.Monads.Helpers;
using Microsoft.SharePoint.Client.InformationPolicy;
using System.Collections.Generic;

namespace Cumulus.Monads.SharePoint
{
    public static class SetSiteReadOnly
    {
        [FunctionName("SetSiteReadOnly")]
        [ResponseType(typeof(SetSiteReadOnlyResponse))]
        [Display(Name = "Set the site to read-only", Description = "Removes everyone from members/owner groups, and adds them or everyone to the visitors group (depending on the group being Private or Public)")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "post")]SetSiteReadOnlyRequest request, TraceWriter log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.SiteURL))
                {
                    throw new ArgumentException("Parameter cannot be null", "SiteURL");
                }
                if (string.IsNullOrWhiteSpace(request.Owner))
                {
                    throw new ArgumentException("Parameter cannot be null", "Owner");
                }

                var clientContext = await ConnectADAL.GetClientContext(request.SiteURL, log);
                var web = clientContext.Web;
                var webProperties = web.AllProperties;
                var siteUsers = web.SiteUsers;
                var associatedVisitorGroup = web.AssociatedVisitorGroup;
                var associatedMemberGroup = web.AssociatedMemberGroup;
                var associatedOwnerGroup = web.AssociatedOwnerGroup;

                const string everyoneIdent = "c:0-.f|rolemanager|spo-grid-all-users/";

                clientContext.Load(webProperties);
                clientContext.Load(siteUsers);
                clientContext.Load(associatedVisitorGroup, g => g.Title, g => g.Users);
                clientContext.Load(associatedMemberGroup, g => g.Title, g => g.Users);
                clientContext.Load(associatedOwnerGroup, g => g.Title, g => g.Users);
                clientContext.ExecuteQueryRetry();

                var visitors = associatedVisitorGroup.Users;
                var members = associatedMemberGroup.Users;
                var owners = associatedOwnerGroup.Users;

                var visitorsPrivate = new List<User>();

                for (var i = 0; i < visitors.Count; i++)
                {
                    log.Info($"Removing {visitors[i].LoginName} from {associatedVisitorGroup.Title}");
                    web.RemoveUserFromGroup(associatedVisitorGroup, visitors[i]);
                }

                for (var i = 0; i < members.Count; i++)
                {
                    visitorsPrivate.Add(members[i]);
                    log.Info($"Removing {members[i].LoginName} from {associatedMemberGroup.Title}");
                    web.RemoveUserFromGroup(associatedMemberGroup, members[i]);
                }

                for (var i = 0; i < owners.Count; i++)
                {
                    visitorsPrivate.Add(owners[i]);
                    log.Info($"Removing {owners[i].LoginName} from {associatedOwnerGroup.Title}");
                    web.RemoveUserFromGroup(associatedOwnerGroup, owners[i]);
                }

                clientContext.ExecuteQueryRetry();

                log.Info($"Adding {request.Owner} to {associatedOwnerGroup.Title}");
                web.AddUserToGroup(associatedOwnerGroup, request.Owner);


                if (webProperties.FieldValues.ContainsKey("GroupType") && webProperties.FieldValues["GroupType"].ToString().Equals("Private"))
                {
                    log.Info($"The site is connected to a private group. Adding existing members/owners to {associatedVisitorGroup.Title}");
                    for (var i = (visitorsPrivate.Count - 1); i >= 0; i--)
                    {
                        log.Info($"Adding {visitorsPrivate[i].LoginName} to {associatedVisitorGroup.Title}");
                        web.AddUserToGroup(associatedVisitorGroup, visitorsPrivate[i]);
                    }
                }
                else
                {
                    log.Info($"The site is standalone or connected to a public group. Adding Everyone but external users to {associatedVisitorGroup.Title}");
                    foreach (User user in siteUsers)
                    {
                        if (user.LoginName.StartsWith(everyoneIdent))
                        {
                            log.Info($"Adding {user.LoginName} to {associatedVisitorGroup.Title}");
                            web.AddUserToGroup(associatedVisitorGroup, user);
                        }
                    }
                }

                clientContext.ExecuteQueryRetry();

                return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ObjectContent<SetSiteReadOnlyResponse>(new SetSiteReadOnlyResponse { SetReadOnly = true }, new JsonMediaTypeFormatter())
                });
            }
            catch (Exception e)
            {
                log.Info(e.StackTrace);
                log.Error($"Error: {e.Message }\n\n{e.StackTrace}");
                return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new ObjectContent<string>(e.Message, new JsonMediaTypeFormatter())
                });
            }
        }

        public class SetSiteReadOnlyRequest
        {
            [Required]
            [Display(Description = "URL of site")]
            public string SiteURL { get; set; }
            [Required]
            [Display(Description = "Owner")]
            public string Owner { get; set; }
        }

        public class SetSiteReadOnlyResponse
        {
            [Display(Description = "True if the site was set to read-only")]
            public bool SetReadOnly { get; set; }
        }
    }
}
