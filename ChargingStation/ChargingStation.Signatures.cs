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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.Node.Web;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Which keys sign as which role of this station, and how it holds what
    /// it receives to its signature policy: what the station is given by
    /// signed role assignments and a signed policy table, kept beside its
    /// configuration file, and what it is told of the mode of the policy
    /// (white paper "Signed OCPP Messages and Signer Roles", chapter 6 and
    /// phase 4 of chapter 7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is one table of signer roles for the station, and both of its
    /// OCPP nodes ask it: a key that signs as the support does so over OCPP
    /// 2.1 and over OCPP 1.6. Whatever writes it comes through the table -
    /// AddSignerRole of either node, a file read at the page or at the
    /// console - so that an assignment is held to the same rules wherever it
    /// comes from.
    /// </para>
    /// <para>
    /// The owner is the role "systemadmin". The first owner key is installed
    /// at the station - or, only where the station is told so and while it
    /// has none, by the first assignment a key of its own signed.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// The directory beside the configuration file the signer roles are kept in.
        /// </summary>
        public const String SignaturesDirectoryName  = "signatures";

        /// <summary>
        /// The file the signer roles, the policy table and the settings are kept in.
        /// </summary>
        public const String SignerRolesFileName      = "signer-roles.json";

        private readonly Lock    signaturesLock      = new();

        private          String? signerRolesNotSaved;

        /// <summary>
        /// The signatures the nodes of this station verify.
        /// </summary>
        private static readonly String[] signatureAlgorithms = [ JWS.ES256, JWS.ES384, JWS.ES512 ];

        /// <summary>
        /// The role of the owner, as the paper spells it for a node.
        /// </summary>
        private const String OwnerRole = "systemadmin";

        #endregion

        #region Properties

        /// <summary>
        /// Which keys sign as which role of this station.
        /// </summary>
        public SignerRoleTable    SignerRoles          { get; private set; } = null!;

        /// <summary>
        /// How this station holds what it receives to its signature policy.
        /// </summary>
        public SignatureSettings  SignatureSettings    { get; private set; } = new();

        private String SignerRolesFile

            => Path.Combine(
                   Path.GetDirectoryName(Path.GetFullPath(ConfigFile.Path)) ?? ".",
                   SignaturesDirectoryName,
                   SignerRolesFileName
               );

        #endregion


        #region (private) RoleOfTheStation(Name) / PermissionsOfARole(Name)

        /// <summary>
        /// What a role of this station's role model may do, or null for a
        /// role it does not have.
        /// </summary>
        private IEnumerable<String>? PermissionsOfARole(String Name)

            => Access.RoleNamed(Name) is Role known
                   ? [.. known.Permissions.Select(permission => permission.ToString())]
                   : null;

        #endregion

        #region (private) LoadSignerRoles()

        /// <summary>
        /// Make the table of signer roles of this station, and read what was
        /// kept of it. Before its OCPP nodes are built, which are given it.
        /// </summary>
        private void LoadSignerRoles()
        {

            SignerRoles = new SignerRoleTable(PermissionsOfARole) {
                              OwnerRole  = OwnerRole,
                              Now        = () => TimeProvider.GetUtcNow()
                          };

            var file = SignerRolesFile;

            if (File.Exists(file))
            {
                try
                {

                    var kept = SignedDataObject.ParseAsReceived(File.ReadAllText(file));

                    if (kept["settings"] is JObject settings)
                    {
                        if (SignatureSettings.TryUpdate(settings, out var read, out var notRead))
                            SignatureSettings = read;
                        else
                            Log.Error($"The signature settings in '{file}' cannot be read, and the defaults apply: {notRead}", "ocpp", "signatures");
                    }

                    if (kept["table"] is JObject table && !SignerRoles.TryLoad(table, out var notLoaded))
                        Log.Error($"The signer roles in '{file}' cannot be read, and no key signs as any role: {notLoaded}", "ocpp", "signatures");

                }
                catch (Exception e)
                {
                    Log.Error($"The signer roles in '{file}' cannot be read, and no key signs as any role: {e.Message}", "ocpp", "signatures");
                }
            }

            ApplySignatureSettingsToTheTable();

            SignerRoles.OnChanged += SignerRolesChanged;

        }

        #endregion

        #region (private) TrySaveSignerRoles(out Error)

        /// <summary>
        /// Write the signer roles, the policy table and the settings beside
        /// the configuration file: to a file of their own, then moved in the
        /// place of the one that is there.
        /// </summary>
        private Boolean TrySaveSignerRoles([NotNullWhen(false)] out String? Error)
        {

            lock (signaturesLock)
            {
                try
                {

                    var file       = SignerRolesFile;
                    var temporary  = file + ".tmp";

                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);

                    if (File.Exists(temporary))
                        File.Delete(temporary);

                    File.WriteAllText(temporary,
                                      new JObject(
                                          new JProperty("settings",  SignatureSettings.ToJSON()),
                                          new JProperty("table",     SignerRoles.ToJSON())
                                      ).ToString(Formatting.Indented));

                    File.Move(temporary, file, overwrite: true);

                    signerRolesNotSaved  = null;
                    Error                = null;
                    return true;

                }
                catch (Exception e)
                {
                    Error                = $"The signer roles could not be written: {e.Message}";
                    signerRolesNotSaved  = Error;
                    return false;
                }
            }

        }

        #endregion

        #region (private) SignerRolesChanged(Change)

        /// <summary>
        /// A role got or lost its keys, or the policy was set: it is written
        /// down, and the policy is the one of both nodes - of the one that
        /// was not asked as well. The nodes tell their CSMS themselves.
        /// </summary>
        private void SignerRolesChanged(SignerRoleTableChange Change)
        {

            if (!TrySaveSignerRoles(out var notSaved))
                Log.Error($"{notSaved} What was changed holds until this station is started again.", "ocpp", "signatures");

            if (Change.Change is SignerRoleTableChanges.PolicySet or SignerRoleTableChanges.TableReset)
                ApplyThePolicyTable();

            Log.Notice(Change.Change switch {
                           SignerRoleTableChanges.RoleAdded            => $"The role '{Change.Role}' got its keys, version {Change.Version}",
                           SignerRoleTableChanges.RoleUpdated          => $"The role '{Change.Role}' got other keys, version {Change.Version}",
                           SignerRoleTableChanges.RoleDeleted          => $"The role '{Change.Role}' lost its keys, version {Change.Version}",
                           SignerRoleTableChanges.FirstOwnerInstalled  => $"The first owner: the role '{Change.Role}' got its keys, version {Change.Version}",
                           SignerRoleTableChanges.TableReset           => "The keys of every role and the signature policy were forgotten",
                           SignerRoleTableChanges.PolicySet            => $"The signature policy was set, version {Change.Version}",
                           _                                           => Change.Change.ToString()
                       } + (Change.Locally ? ", at the station itself." : ", over OCPP."),
                       "ocpp", "signatures");

        }

        #endregion

        #region (private) ApplyThePolicyTable() / ApplySignatureSettings...()

        /// <summary>
        /// Make the entries of the policy table this station was given those
        /// of both of its nodes.
        /// </summary>
        private void ApplyThePolicyTable()
        {

            foreach (var (policy, version) in new[] { (cs02?.OCPP.SignaturePolicy, "OCPP 2.1"), (cs01?.OCPP.SignaturePolicy, "OCPP 1.6") })
                if (policy is not null && SignerRoles.ApplyPolicyTo(policy) is String whyNot)
                    Log.Warning($"The policy table this station was given is not the one of its {version} node: {whyNot}", "ocpp", "signatures");

        }

        private void ApplySignatureSettingsToTheTable()
        {
            SignerRoles.AllowFirstOwnerByNetwork   = SignatureSettings.AllowFirstOwnerByCSMS;
            SignerRoles.AllowPermissionsByNetwork  = SignatureSettings.AllowPermissionsByCSMS;
            SignerRoles.Quorum                     = SignatureSettings.Quorum;
            SignerRoles.MaxClockSkew               = SignatureSettings.MaxClockSkew;
        }

        private void ApplySignatureSettingsTo(SignaturePolicy Policy)
        {
            Policy.Mode             = SignatureSettings.Mode;
            Policy.AcceptFormat0    = SignatureSettings.AcceptFormat0;
            Policy.MaxClockSkew     = SignatureSettings.MaxClockSkew;
            Policy.MaxSignatureAge  = SignatureSettings.MaxSignatureAge;
        }

        #endregion

        #region (private) ShareTheSignerRolesWith(Node)

        /// <summary>
        /// Give an OCPP node of this station its table of signer roles, the
        /// policy table that came with them, and the settings of the policy.
        /// </summary>
        private void ShareTheSignerRolesWith(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            Node.SignerRoles                                = SignerRoles;
            Node.OCPP.SignaturePolicy.PermissionsOfRole     = role => SignerRoles.PermissionsOf(role);

            ApplySignatureSettingsTo(Node.OCPP.SignaturePolicy);

            if (SignerRoles.ApplyPolicyTo(Node.OCPP.SignaturePolicy) is String whyNot)
                Log.Warning($"The policy table this station was given is not the one of its OCPP 2.1 node: {whyNot}", "ocpp", "signatures");

            AttachTheSignatureComponents(Node);

        }

        private void ShareTheSignerRolesWith(OCPPv1_6.TestChargePointNode Node)
        {

            Node.SignerRoles                                = SignerRoles;
            Node.OCPP.SignaturePolicy.PermissionsOfRole     = role => SignerRoles.PermissionsOf(role);

            ApplySignatureSettingsTo(Node.OCPP.SignaturePolicy);

            if (SignerRoles.ApplyPolicyTo(Node.OCPP.SignaturePolicy) is String whyNot)
                Log.Warning($"The policy table this station was given is not the one of its OCPP 1.6 node: {whyNot}", "ocpp", "signatures");

            Node.AddConfigurationKeySource(new SignatureKeys(this));

        }

        /// <summary>
        /// Take the table of signer roles from a node that is replaced, so
        /// that it tells nobody of a change any more.
        /// </summary>
        private static void TakeTheSignerRolesFrom(protocols.WWCP.NetworkingNode.AWWCPNetworkingNode? Node)
        {
            if (Node is not null)
                Node.SignerRoles = new SignerRoleTable(role => null);
        }

        /// <summary>
        /// Whether this station has no owner yet and takes its first one over OCPP.
        /// </summary>
        private Boolean TakesItsFirstOwner
            => SignerRoles.AllowFirstOwnerByNetwork && !SignerRoles.HasOwner;

        #endregion


        #region SignaturesJSON()

        /// <summary>
        /// What this station knows of signed messages: the settings of the
        /// policy, the policy table it was given, and its roles with the keys
        /// that sign as them.
        /// </summary>
        public JObject SignaturesJSON()
        {

            var assignments = SignerRoles.Assignments.ToDictionary(assignment => assignment.Role, StringComparer.Ordinal);

            JObject RoleJSON(String Name, String? Description)
            {

                assignments.TryGetValue(Name, out var assignment);

                var json = new JObject(
                               new JProperty("role",         Name),
                               new JProperty("permissions",  new JArray(SignerRoles.PermissionsOf(Name))),
                               new JProperty("version",      SignerRoles.VersionOf(Name)),
                               new JProperty("keys",         new JArray((assignment?.Keys ?? []).Select(key => {

                                                                 var keyJSON = new JObject(new JProperty("kid", key.KeyId));

                                                                 if (key.Label is not null)
                                                                     keyJSON.Add("label",     key.Label);

                                                                 if (key.NotBefore.HasValue)
                                                                     keyJSON.Add("notBefore", key.NotBefore.Value.ToISO8601());

                                                                 if (key.NotAfter.HasValue)
                                                                     keyJSON.Add("notAfter",  key.NotAfter. Value.ToISO8601());

                                                                 return keyJSON;

                                                             })))
                           );

                if ((assignment?.Description ?? Description) is String description)
                    json.Add("description", description);

                if (SignerRoles.LastUsed(Name) is DateTimeOffset lastUsed)
                    json.Add("lastUsed", lastUsed.ToISO8601());

                // As it was received, with its signatures: what a file of it is.
                if (assignment is not null)
                    json.Add("assignment", assignment.JSON);

                return json;

            }

            var json = new JObject(

                           new JProperty("settings",       SignatureSettings.ToJSON()),
                           new JProperty("algorithms",     new JArray(signatureAlgorithms)),
                           new JProperty("hasOwner",       SignerRoles.HasOwner),
                           new JProperty("ownerRole",      OwnerRole),
                           new JProperty("policyVersion",  SignerRoles.PolicyVersion),

                           new JProperty("roles",          new JArray(
                                                               Access.Roles.
                                                                   Select(role => RoleJSON(role.Name, role.Description)).
                                                                   Concat(assignments.Values.
                                                                              Where (assignment => Access.RoleNamed(assignment.Role) is null).
                                                                              Select(assignment => RoleJSON(assignment.Role, null)))
                                                           ))

                       );

            if (SignerRoles.Policy is JObject policy)
                json.Add("policy", policy);

            if (signerRolesNotSaved is not null)
                json.Add("notSaved", signerRolesNotSaved);

            return json;

        }

        #endregion

        #region TryUpdateSignatureSettings(JSON, out Error, out NotSaved)

        /// <summary>
        /// Change how this station holds what it receives to its signature
        /// policy: of what the given object says, and of nothing else.
        /// </summary>
        /// <param name="JSON">The settings that change.</param>
        /// <param name="Error">Why nothing was changed.</param>
        /// <param name="NotSaved">Whether it could not be written down.</param>
        public Boolean TryUpdateSignatureSettings(JObject                           JSON,
                                                  [NotNullWhen(false)] out String?  Error,
                                                  out Boolean                       NotSaved)
        {

            NotSaved = false;

            lock (signaturesLock)
            {

                if (!SignatureSettings.TryUpdate(JSON, out var wanted, out Error))
                    return false;

                if (wanted == SignatureSettings)
                    return true;

                var before = SignatureSettings;

                SignatureSettings = wanted;

                if (!TrySaveSignerRoles(out Error))
                {
                    SignatureSettings  = before;
                    NotSaved           = true;
                    return false;
                }

                ApplySignatureSettingsToTheTable();

                if (cs02 is not null)
                    ApplySignatureSettingsTo(cs02.OCPP.SignaturePolicy);

                if (cs01 is not null)
                    ApplySignatureSettingsTo(cs01.OCPP.SignaturePolicy);

                Log.Notice($"The signature policy of this station: {wanted.ToJSON().ToString(Formatting.None)}", "ocpp", "signatures");

                return true;

            }

        }

        #endregion

        #region TryImportSignerRole(JSON, out Reason, out Error) / TryInstallFirstOwner / TryDeleteSignerRole / ResetSignerRoles / TryImportSignaturePolicy

        /// <summary>
        /// Take a role assignment read at the station: held to what one
        /// received over OCPP is held to.
        /// </summary>
        public Boolean TryImportSignerRole(JObject                           JSON,
                                           out SignerRoleReasons             Reason,
                                           [NotNullWhen(false)] out String?  Error)

            => SignerRoles.TryImport(JSON, out Reason, out Error!, Locally: true);

        /// <summary>
        /// Make the first owner of this station, at the station: a role
        /// assignment for "systemadmin", which nobody could have signed yet.
        /// </summary>
        public Boolean TryInstallFirstOwner(JObject                           JSON,
                                            out SignerRoleReasons             Reason,
                                            [NotNullWhen(false)] out String?  Error)

            => SignerRoles.TryInstallFirstOwner(JSON, out Reason, out Error!);

        /// <summary>
        /// Make the first owner of this station from its public key alone, at
        /// the station: the role assignment for "systemadmin" is written here,
        /// in the next version, with the one key.
        /// </summary>
        /// <param name="JWK">The public key of the owner, as a JSON Web Key.</param>
        /// <param name="Label">Whose key it is.</param>
        public Boolean TryInstallFirstOwnerKey(JObject                           JWK,
                                               String?                           Label,
                                               out SignerRoleReasons             Reason,
                                               [NotNullWhen(false)] out String?  Error)
        {

            if (JWK["d"] is not null)
            {
                Reason  = SignerRoleReasons.InvalidKey;
                Error   = "This is a private key: the station is given the public key of its owner, and nothing else.";
                return false;
            }

            if (!JWS.TryParsePublicKey(JWK, out var publicKey) || publicKey is null)
            {
                Reason  = SignerRoleReasons.InvalidKey;
                Error   = "This is no public key of a curve the station knows: P-256, P-384 or P-521, as a JSON Web Key.";
                return false;
            }

            return TryInstallFirstOwner(
                       SignerRoleAssignment.Create(
                           OwnerRole,
                           SignerRoles.VersionOf(OwnerRole) + 1,
                           [ (publicKey, Label, null, null) ],
                           Issued: TimeProvider.GetUtcNow()
                       ),
                       out Reason,
                       out Error
                   );

        }

        /// <summary>
        /// Take the keys of a role away, at the station.
        /// </summary>
        public Boolean TryDeleteSignerRole(String                            Role,
                                           UInt64                            Version,
                                           out SignerRoleReasons             Reason,
                                           [NotNullWhen(false)] out String?  Error)

            => SignerRoles.TryDelete(Role, Version, out Reason, out Error!, Locally: true);

        /// <summary>
        /// Take a policy table read at the station: held to what one received
        /// over OCPP is held to.
        /// </summary>
        public Boolean TryImportSignaturePolicy(JObject                           JSON,
                                                out SignerRoleReasons             Reason,
                                                [NotNullWhen(false)] out String?  Error)

            => SignerRoles.TrySetPolicy(JSON, cs02.OCPP.SignaturePolicy, out Reason, out Error!, Locally: true);

        /// <summary>
        /// Forget every key of every role, and the policy table: the way out
        /// for a station whose owners lost their keys.
        /// </summary>
        public void ResetSignerRoles()
            => SignerRoles.Reset();

        #endregion


        #region (private) AttachTheSignatureComponents(Node)

        /// <summary>
        /// Give the OCPP 2.1 node its MessageSignatureController, and a
        /// SignerRoleController for every role of this station.
        /// </summary>
        private void AttachTheSignatureComponents(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            var source = new SignatureSource(this);

            if (!Node.GetComponentConfigs(nameof(OCPPv2_1.MessageSignatureController)).Any())
                Node.AddComponent(new OCPPv2_1.MessageSignatureController(source));

            foreach (var role in source.Roles)
                if (!Node.GetComponentConfigs(nameof(OCPPv2_1.SignerRoleController)).OfType<OCPPv2_1.SignerRoleController>().Any(shown => shown.Role == role))
                    Node.AddComponent(new OCPPv2_1.SignerRoleController(source, role));

        }

        #endregion

        #region (private) SignatureValue(Variable) / SignerRoleValue(Role, Variable) / TrySetSignatureValue(Variable, Value, ...)

        private String? SignatureValue(String Variable)

            => Variable switch {
                   "Mode"                   => SignatureSettings.Mode.ToString(),
                   "AcceptFormat0"          => SignatureSettings.AcceptFormat0 ? "true" : "false",
                   "Algorithms"             => String.Join(",", signatureAlgorithms),
                   "MaxClockSkew"           => ((Int64) SignatureSettings.MaxClockSkew.   TotalSeconds).ToString(CultureInfo.InvariantCulture),
                   "MaxSignatureAge"        => ((Int64) SignatureSettings.MaxSignatureAge.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                   "Roles"                  => String.Join(",", Access.Roles.Select(role => role.Name)),
                   "PolicyVersion"          => SignerRoles.PolicyVersion.ToString(CultureInfo.InvariantCulture),
                   "AllowFirstOwnerByCSMS"  => SignatureSettings.AllowFirstOwnerByCSMS ? "true" : "false",
                   _                        => null
               };

        private String? SignerRoleValue(String Role, String Variable)
        {

            var assignment = SignerRoles.Assignments.FirstOrDefault(assignment => assignment.Role == Role);

            return Variable switch {
                       "Description"  => assignment?.Description ?? Access.RoleNamed(Role)?.Description ?? "",
                       "Permissions"  => String.Join(",", SignerRoles.PermissionsOf(Role)),
                       "KeyIds"       => String.Join(",", (assignment?.Keys ?? []).Select(key => key.KeyId)),
                       "Version"      => SignerRoles.VersionOf(Role).ToString(CultureInfo.InvariantCulture),
                       "LastUsed"     => SignerRoles.LastUsed(Role)?.ToISO8601() ?? "",
                       _              => null
                   };

        }

        /// <summary>
        /// The name a variable of MessageSignatureController has among the settings.
        /// </summary>
        private static String? SettingOf(String Variable)

            => Variable switch {
                   "Mode"             => "mode",
                   "AcceptFormat0"    => "acceptFormat0",
                   "MaxClockSkew"     => "maxClockSkew",
                   "MaxSignatureAge"  => "maxSignatureAge",
                   _                  => null
               };

        #endregion

        #region (private class) SignatureSource

        /// <summary>
        /// What MessageSignatureController and SignerRoleController read and
        /// write of this station.
        /// </summary>
        private sealed class SignatureSource(ChargingStation Station) : OCPPv2_1.IMessageSignatureSource,
                                                                        OCPPv2_1.ISetVariablesSource
        {

            public IEnumerable<String> Variables          => [ "Mode", "AcceptFormat0", "Algorithms", "MaxClockSkew", "MaxSignatureAge", "Roles", "PolicyVersion", "AllowFirstOwnerByCSMS" ];

            public IEnumerable<String> WritableVariables  => [ "Mode", "AcceptFormat0", "MaxClockSkew", "MaxSignatureAge" ];

            public IEnumerable<String> Roles              => [.. Station.Access.Roles.Select(role => role.Name)];

            public IEnumerable<String> RoleVariables      => [ "Description", "Permissions", "KeyIds", "Version", "LastUsed" ];

            public String? Value(String Variable)
                => Station.SignatureValue(Variable);

            public String? RoleValue(String Role, String Variable)
                => Station.SignerRoleValue(Role, Variable);

            public Task<IReadOnlyList<OCPPv2_1.SetVariableResult>> SetVariablesAsync(IReadOnlyList<OCPPv2_1.SetVariableEntry>  Entries,
                                                                                     OCPPv2_1.DataConsistencyModel             DataConsistencyModel,
                                                                                     CancellationToken                         CancellationToken)
            {

                var results = new OCPPv2_1.SetVariableResult[Entries.Count];

                OCPPv2_1.SetVariableResult Result(Int32 Index, OCPPv2_1.SetVariableStatus Status, String? ReasonCode = null, String? AdditionalInfo = null)
                    => results[Index] = new (
                                            AttributeStatus:      Status,
                                            Component:            Entries[Index].Data.Component,
                                            Variable:             Entries[Index].Data.Variable,
                                            AttributeType:        Entries[Index].Variable.Attributes?.Type ?? OCPPv2_1.AttributeTypes.Actual,
                                            AttributeStatusInfo:  ReasonCode is not null ? new OCPPv2_1.StatusInfo(ReasonCode, AdditionalInfo) : null
                                        );

                // All of the request's at once: either every setting it names is
                // changed and written down, or none.
                var wanted   = new JObject();
                var checkedOK = true;

                for (var index = 0; index < Entries.Count; index++)
                {

                    var name   = Entries[index].Variable.Name;
                    var value  = Entries[index].Data.AttributeValue;

                    if (!OCPPv2_1.MessageSignatureController.TryCheck(name, value, out var refusal))
                    {
                        Result(index, OCPPv2_1.SetVariableStatus.Rejected, refusal.ReasonCode, refusal.AdditionalInfo);
                        checkedOK = false;
                    }

                    else if (SettingOf(name) is not String setting)
                    {
                        Result(index, OCPPv2_1.SetVariableStatus.Rejected, "NotSupported", $"MessageSignatureController.{name} is not written at this station.");
                        checkedOK = false;
                    }

                    else
                        wanted[setting] = value;

                }

                if (checkedOK && !Station.TryUpdateSignatureSettings(wanted, out var error, out var notSaved))
                {
                    for (var index = 0; index < Entries.Count; index++)
                        Result(index, OCPPv2_1.SetVariableStatus.Rejected, notSaved ? "InternalError" : "InvalidValue", error);
                }

                else
                    for (var index = 0; index < Entries.Count; index++)
                        results[index] ??= checkedOK
                                               ? Result(index, OCPPv2_1.SetVariableStatus.Accepted)
                                               : Result(index, OCPPv2_1.SetVariableStatus.Rejected, "NotApplied", "Another setting of this request was refused, and none of it was changed.");

                return Task.FromResult<IReadOnlyList<OCPPv2_1.SetVariableResult>>(results);

            }

        }

        #endregion

        #region (private class) SignatureKeys

        /// <summary>
        /// The configuration keys of OCPP 1.6 that stand for the variables of
        /// MessageSignatureController and SignerRoleController (section 6.4):
        /// "signatures.mode", and "signatures.roles.&lt;role&gt;.version".
        /// </summary>
        private sealed class SignatureKeys(ChargingStation Station) : OCPPv1_6.CP.IConfigurationKeySource
        {

            private static readonly (String Key, String Variable, Boolean Writable)[] keys = [
                ("signatures.mode",                   "Mode",                   true),
                ("signatures.acceptFormat0",          "AcceptFormat0",          true),
                ("signatures.algorithms",             "Algorithms",             false),
                ("signatures.maxClockSkew",           "MaxClockSkew",           true),
                ("signatures.maxSignatureAge",        "MaxSignatureAge",        true),
                ("signatures.roles",                  "Roles",                  false),
                ("signatures.policyVersion",          "PolicyVersion",          false),
                ("signatures.allowFirstOwnerByCSMS",  "AllowFirstOwnerByCSMS",  false)
            ];

            private static readonly (String Suffix, String Variable)[] roleKeys = [
                ("permissions",  "Permissions"),
                ("keyIds",       "KeyIds"),
                ("version",      "Version"),
                ("lastUsed",     "LastUsed")
            ];

            private const String RolesPrefix = "signatures.roles.";

            public IEnumerable<String> Keys

                => [.. keys.Select(key => key.Key),
                    .. Station.Access.Roles.SelectMany(role => roleKeys.Select(roleKey => $"{RolesPrefix}{role.Name}.{roleKey.Suffix}"))];

            public OCPPv1_6.ConfigurationKey? Get(String Key)
            {

                if (keys.FirstOrDefault(known => String.Equals(known.Key, Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } key)
                    return new OCPPv1_6.ConfigurationKey(
                               key.Key,
                               key.Writable ? OCPPv1_6.AccessRights.ReadWrite : OCPPv1_6.AccessRights.ReadOnly,
                               Station.SignatureValue(key.Variable)
                           );

                if (RoleKey(Key) is var (role, suffix, variable))
                    return new OCPPv1_6.ConfigurationKey(
                               $"{RolesPrefix}{role}.{suffix}",
                               OCPPv1_6.AccessRights.ReadOnly,
                               Station.SignerRoleValue(role, variable)
                           );

                return null;

            }

            private (String Role, String Suffix, String Variable)? RoleKey(String Key)
            {

                if (!Key.StartsWith(RolesPrefix, StringComparison.OrdinalIgnoreCase))
                    return null;

                var rest  = Key[RolesPrefix.Length..];
                var dot   = rest.LastIndexOf('.');

                if (dot <= 0)
                    return null;

                var role     = Station.Access.Roles.FirstOrDefault(role => String.Equals(role.Name, rest[..dot], StringComparison.OrdinalIgnoreCase));
                var roleKey  = roleKeys.FirstOrDefault(roleKey => String.Equals(roleKey.Suffix, rest[(dot + 1)..], StringComparison.OrdinalIgnoreCase));

                return role is not null && roleKey.Suffix is not null
                           ? (role.Name, roleKey.Suffix, roleKey.Variable)
                           : null;

            }

            public Task<OCPPv1_6.ConfigurationStatus?> ChangeAsync(String             Key,
                                                                   String             Value,
                                                                   String?            Sender,
                                                                   CancellationToken  CancellationToken = default)
            {

                OCPPv1_6.ConfigurationStatus? status;

                if (keys.FirstOrDefault(known => String.Equals(known.Key, Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } key)
                    status = key.Writable                                                                   &&
                             OCPPv2_1.MessageSignatureController.TryCheck(key.Variable, Value, out _)       &&
                             SettingOf(key.Variable) is String setting                                      &&
                             Station.TryUpdateSignatureSettings(new JObject(new JProperty(setting, Value)), out _, out _)
                                 ? OCPPv1_6.ConfigurationStatus.Accepted
                                 : OCPPv1_6.ConfigurationStatus.Rejected;

                // What a role may do and which keys it has is written by a role assignment.
                else if (RoleKey(Key) is not null)
                    status = OCPPv1_6.ConfigurationStatus.Rejected;

                else
                    status = null;

                return Task.FromResult(status);

            }

        }

        #endregion

    }


    #region SignatureSettings

    /// <summary>
    /// How a station holds what it receives to its signature policy: what
    /// MessageSignatureController says of it (white paper "Signed OCPP
    /// Messages and Signer Roles", section 6.3.1), and what is decided at the
    /// station alone.
    /// </summary>
    /// <param name="Mode">Off: signatures are ignored. Observe: what the policy would refuse is only reported. Enforce: the policy applies.</param>
    /// <param name="AcceptFormat0">Whether a signature of the format before the protected header is accepted, as unverified.</param>
    /// <param name="MaxClockSkew">How far in the future a signature may say it was made.</param>
    /// <param name="MaxSignatureAge">How long a signature that does not say when it expires is valid.</param>
    /// <param name="AllowFirstOwnerByCSMS">Whether the station, while it has no owner, takes its first owner over OCPP. Written at the station alone.</param>
    /// <param name="AllowPermissionsByCSMS">Whether a role assignment may say what a role may do beyond what the station says of it (Q4). Written at the station alone.</param>
    /// <param name="Quorum">How many owners have to sign a role assignment or a policy table. Written at the station alone.</param>
    public sealed record SignatureSettings(MessagePolicyModes  Mode                     = MessagePolicyModes.Off,
                                           Boolean             AcceptFormat0            = false,
                                           TimeSpan            MaxClockSkew             = default,
                                           TimeSpan            MaxSignatureAge          = default,
                                           Boolean             AllowFirstOwnerByCSMS    = false,
                                           Boolean             AllowPermissionsByCSMS   = false,
                                           UInt32              Quorum                   = 1)
    {

        /// <summary>
        /// How far in the future a signature may say it was made: five minutes, where nothing is said.
        /// </summary>
        public TimeSpan MaxClockSkew     { get; init; } = MaxClockSkew    == default ? TimeSpan.FromSeconds(300) : MaxClockSkew;

        /// <summary>
        /// How long a signature that does not say when it expires is valid: fifteen minutes, where nothing is said.
        /// </summary>
        public TimeSpan MaxSignatureAge  { get; init; } = MaxSignatureAge == default ? TimeSpan.FromSeconds(900) : MaxSignatureAge;

        /// <summary>
        /// The settings as JSON.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("mode",                    Mode.ToString()),
                   new JProperty("acceptFormat0",           AcceptFormat0),
                   new JProperty("maxClockSkew",            (Int64) MaxClockSkew.   TotalSeconds),
                   new JProperty("maxSignatureAge",         (Int64) MaxSignatureAge.TotalSeconds),
                   new JProperty("allowFirstOwnerByCSMS",   AllowFirstOwnerByCSMS),
                   new JProperty("allowPermissionsByCSMS",  AllowPermissionsByCSMS),
                   new JProperty("quorum",                  Quorum)
               );

        /// <summary>
        /// These settings, with what the given object says in the place of
        /// what they say. A value is what JSON has for it, or the text a
        /// variable of OCPP has for it.
        /// </summary>
        /// <param name="JSON">The settings that change.</param>
        /// <param name="Settings">The settings afterwards.</param>
        /// <param name="Error">Why the object says nothing these settings could say.</param>
        public Boolean TryUpdate(JObject                                      JSON,
                                 [NotNullWhen(true)]  out SignatureSettings?  Settings,
                                 [NotNullWhen(false)] out String?             Error)
        {

            Settings  = null;
            Error     = null;

            var settings = this;

            foreach (var property in JSON.Properties())
            {

                var text = property.Value.Type switch {
                               JTokenType.String   => property.Value.Value<String>(),
                               JTokenType.Boolean  => property.Value.Value<Boolean>() ? "true" : "false",
                               JTokenType.Integer  => property.Value.ToString(),
                               _                   => null
                           };

                if (text is null)
                {
                    Error = $"'{property.Name}' is neither a text, a number, nor true or false.";
                    return false;
                }

                switch (property.Name)
                {

                    case "mode":
                        if (!Enum.TryParse<MessagePolicyModes>(text, ignoreCase: false, out var mode) || !Enum.IsDefined(mode) || Int32.TryParse(text, out _))
                        {
                            Error = $"'{text}' is no mode of the signature policy: Off, Observe or Enforce.";
                            return false;
                        }
                        settings = settings with { Mode = mode };
                        break;

                    case "acceptFormat0":
                    case "allowFirstOwnerByCSMS":
                    case "allowPermissionsByCSMS":
                        if (text is not ("true" or "false"))
                        {
                            Error = $"'{property.Name}' is true or false, not '{text}'.";
                            return false;
                        }
                        settings = property.Name switch {
                                       "acceptFormat0"          => settings with { AcceptFormat0          = text == "true" },
                                       "allowFirstOwnerByCSMS"  => settings with { AllowFirstOwnerByCSMS  = text == "true" },
                                       _                        => settings with { AllowPermissionsByCSMS = text == "true" }
                                   };
                        break;

                    case "maxClockSkew":
                    case "maxSignatureAge":
                        if (!UInt32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                            seconds > 86400 ||
                           (property.Name == "maxSignatureAge" && seconds == 0))
                        {
                            Error = $"'{property.Name}' is a number of seconds up to a day, not '{text}'.";
                            return false;
                        }
                        settings = property.Name == "maxClockSkew"
                                       ? settings with { MaxClockSkew    = TimeSpan.FromSeconds(seconds) }
                                       : settings with { MaxSignatureAge = TimeSpan.FromSeconds(seconds) };
                        break;

                    case "quorum":
                        if (!UInt32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var quorum) || quorum == 0 || quorum > 16)
                        {
                            Error = $"'quorum' is a number of owners from 1 to 16, not '{text}'.";
                            return false;
                        }
                        settings = settings with { Quorum = quorum };
                        break;

                    default:
                        Error = $"'{property.Name}' is no setting of the signature policy.";
                        return false;

                }

            }

            Settings = settings;
            return true;

        }

    }

    #endregion

}
