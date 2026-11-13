/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Open Charging Station <https://github.com/OpenChargingCloud/ChargingStation>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// signerroles: which keys sign as which role of the station, and how it
    /// holds what it receives to its signature policy (white paper "Signed
    /// OCPP Messages and Signer Roles", section 6.6).
    /// </summary>
    /// <remarks>
    /// What the Signer roles page does, at the permission it asks for. A
    /// role assignment read from a file is checked as one received over
    /// OCPP is; the first owner is the one exception, because there is
    /// nobody yet who could have signed it.
    /// </remarks>
    public class SignerRolesCommand(StationCLI CLI) : ACLICommand<StationCLI>(CLI),
                                                       ICLICommand
    {

        #region Data

        /// <summary>
        /// What is typed: "signerroles".
        /// </summary>
        public static readonly String CommandName = nameof(SignerRolesCommand)[..^7].ToLowerInvariant();

        private static readonly String[] verbs = [ "list", "import", "owner", "remove", "policy", "mode", "reset" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2 && String.Equals(Arguments[0], CommandName, StringComparison.OrdinalIgnoreCase))
                return [.. verbs.Where (verb => verb.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                                 Select(verb => SuggestionResponse.ParameterCompleted($"{CommandName} {verb}"))];

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            var rest   = Arguments.Skip(1).ToArray();
            var verb   = rest.Length == 0 ? "list" : rest[0].ToLowerInvariant();

            var needs  = verb == "list"
                             ? Permission.Read(NodeResources.Signatures)
                             : Permission.Edit(NodeResources.Signatures);

            if (!cli.MayDo(needs, CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var station = cli.Station;

            switch (verb)
            {

                case "list" when rest.Length <= 1:
                    return Task.FromResult(Listed());

                case "import" when rest.Length == 2:
                    return Task.FromResult(FromAFile(rest[1],
                                                     json => station.TryImportSignerRole(json, out var reason, out var error)
                                                                 ? null
                                                                 : $"{reason}: {error}",
                                                     json => $"The role '{json["role"]}' has the keys of version {json["version"]}."));

                case "owner" when rest.Length == 2:
                    return Task.FromResult(FromAFile(rest[1],
                                                     json => (json["role"] is null && json["kty"] is not null
                                                                  ? station.TryInstallFirstOwnerKey(json, null, out var reason, out var error)
                                                                  : station.TryInstallFirstOwner   (json,       out reason,     out error))
                                                                 ? null
                                                                 : $"{reason}: {error}",
                                                     json => "This station has its first owner."));

                case "policy" when rest.Length == 2:
                    return Task.FromResult(FromAFile(rest[1],
                                                     json => station.TryImportSignaturePolicy(json, out var reason, out var error)
                                                                 ? null
                                                                 : $"{reason}: {error}",
                                                     json => $"The policy table of version {json["version"]} is the station's."));

                case "remove" when rest.Length == 3 && UInt64.TryParse(rest[2], out var version):
                    return Task.FromResult<String[]>([
                               station.TryDeleteSignerRole(rest[1], version, out var whyNot, out var notDeleted)
                                   ? $"The role '{rest[1]}' has no keys any more."
                                   : $"{whyNot}: {notDeleted}"
                           ]);

                case "mode" when rest.Length == 2:
                    return Task.FromResult<String[]>([
                               station.TryUpdateSignatureSettings(new JObject(new JProperty("mode", rest[1])), out var notSet, out _)
                                   ? $"The signature policy is '{station.SignatureSettings.Mode}'."
                                   : notSet
                           ]);

                // Typed twice: nobody forgets every key of every role by a slip.
                case "reset" when rest.Length == 2 && rest[1] == "reset":
                    station.ResetSignerRoles();
                    return Task.FromResult<String[]>([ "Every key of every role and the policy table are forgotten. This station has no owner." ]);

                case "reset":
                    return Task.FromResult<String[]>([ $"This forgets every key of every role and the policy table. To do so: {CommandName} reset reset" ]);

                default:
                    return Task.FromResult<String[]>([ $"Usage: {Help()}" ]);

            }

        }

        #endregion

        #region Help()

        public override String Help()
            => $"{CommandName} [list | import <file> | owner <file> | remove <role> <version> | policy <file> | mode <Off|Observe|Enforce> | reset] - which keys sign as which role, and the signature policy";

        #endregion

        #region (private) Listed()

        private String[] Listed()
        {

            var said   = cli.Station.SignaturesJSON();
            var roles  = said["roles"] as JArray ?? [];
            var width  = roles.Select(role => role["role"]!.Value<String>()!.Length).DefaultIfEmpty(0).Max();

            return [

                $"The signature policy is '{said["settings"]?["mode"]}'" +
                    (said["policyVersion"]?.Value<UInt64>() is UInt64 version and > 0 ? $", with the policy table of version {version}." : ", without a policy table."),

                said["hasOwner"]?.Value<Boolean>() == true
                    ? $"The owner is the role '{said["ownerRole"]}'."
                    : $"This station has no owner yet: the first one is made with '{CommandName} owner <file>'" +
                          (said["settings"]?["allowFirstOwnerByCSMS"]?.Value<Boolean>() == true ? ", or comes from the CSMS." : "."),

                .. roles.Select(role => {

                       var keys = role["keys"] as JArray ?? [];

                       return $"{role["role"]!.Value<String>()!.PadRight(width)}  " +
                              (keys.Count == 0
                                   ? "no keys"
                                   : $"version {role["version"]}: " +
                                     String.Join(", ", keys.Select(key => key["kid"]!.Value<String>() + (key["label"] is JToken label ? $" ({label})" : ""))));

                   })

            ];

        }

        #endregion

        #region (private static) FromAFile(Path, Take, Said)

        /// <summary>
        /// Read a signed data object from a file, as it is written there, and
        /// hand it on: what comes back is why it was not taken, or null.
        /// </summary>
        private static String[] FromAFile(String                 Path,
                                          Func<JObject, String?>  Take,
                                          Func<JObject, String>   Said)
        {

            JObject json;

            try
            {
                json = SignedDataObject.ParseAsReceived(File.ReadAllText(Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return [ $"'{Path}' cannot be read: {e.Message}" ];
            }
            catch (Exception)
            {
                return [ $"'{Path}' is no JSON object." ];
            }

            return [ Take(json) ?? Said(json) ];

        }

        #endregion

    }

}
