/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of ChargingStation <https://github.com/OpenChargingCloud/ChargingStation>
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
using System.Security.Cryptography;

#endregion

namespace cloud.charging.open.ChargingStation.Web
{

    /// <summary>
    /// A password as it may be written down: PBKDF2-SHA256 over a random salt,
    /// in a PHC string that says which parameters produced it.
    /// </summary>
    /// <remarks>
    /// <c>$pbkdf2-sha256$i=210000$&lt;salt&gt;$&lt;hash&gt;</c>, both halves
    /// Base64. The parameters travel with the hash rather than living in this
    /// file alone, so that raising the iteration count tomorrow still leaves
    /// the passwords stored today verifiable.
    ///
    /// This exists next to Hermod's <c>SecurePassword</c>, which hashes and
    /// compares just as well but has no way back from its text form: its
    /// <c>TryParse</c> hashes a plaintext rather than reading a stored hash, so
    /// a password written to a file could never be read in again.
    /// </remarks>
    public sealed class PasswordHash : IEquatable<PasswordHash>
    {

        #region Data

        /// <summary>
        /// The algorithm label of the PHC string.
        /// </summary>
        public  const  String  Algorithm          = "pbkdf2-sha256";

        /// <summary>
        /// The number of PBKDF2 iterations for a password hashed from now on.
        /// OWASP's recommendation for PBKDF2-HMAC-SHA256.
        /// </summary>
        public  const  Int32   DefaultIterations  = 210_000;

        /// <summary>
        /// The number of salt bytes.
        /// </summary>
        public  const  Int32   SaltSize           = 16;

        /// <summary>
        /// The number of hash bytes, the output size of SHA-256.
        /// </summary>
        public  const  Int32   HashSize           = 32;

        private readonly Byte[]  salt;
        private readonly Byte[]  hash;

        #endregion

        #region Properties

        /// <summary>
        /// How many iterations produced this hash - not necessarily
        /// <see cref="DefaultIterations"/>, when it was written long ago.
        /// </summary>
        public Int32  Iterations    { get; }

        #endregion

        #region Constructor(s)

        private PasswordHash(Int32   Iterations,
                             Byte[]  Salt,
                             Byte[]  Hash)
        {
            this.Iterations  = Iterations;
            this.salt        = Salt;
            this.hash        = Hash;
        }

        #endregion


        #region (static) Create  (Password, Iterations = DefaultIterations)

        /// <summary>
        /// Hash a password that somebody chose.
        /// </summary>
        /// <param name="Password">The password in the clear; it is not kept.</param>
        /// <param name="Iterations">The number of PBKDF2 iterations.</param>
        public static PasswordHash Create(String  Password,
                                          Int32   Iterations   = DefaultIterations)
        {

            ArgumentNullException.ThrowIfNull(Password);

            if (Iterations < 1)
                throw new ArgumentOutOfRangeException(nameof(Iterations), "The number of iterations must be positive!");

            var salt = RandomNumberGenerator.GetBytes(SaltSize);

            return new PasswordHash(
                       Iterations,
                       salt,
                       Derive(Password, salt, Iterations, HashSize)
                   );

        }

        #endregion

        #region (static) TryParse(Text, out PasswordHash)

        /// <summary>
        /// Read a PHC string written by <see cref="ToString"/>.
        /// </summary>
        public static Boolean TryParse(String?                                Text,
                                       [NotNullWhen(true)] out PasswordHash?  PasswordHash)
        {

            PasswordHash = null;

            if (String.IsNullOrWhiteSpace(Text))
                return false;

            // "", "pbkdf2-sha256", "i=210000", salt, hash
            var parts = Text.Split('$');

            if (parts.Length    != 5         ||
                parts[0].Length != 0         ||
                parts[1]        != Algorithm ||
                !parts[2].StartsWith("i=", StringComparison.Ordinal))
            {
                return false;
            }

            if (!Int32.TryParse(parts[2].AsSpan(2), out var iterations) || iterations < 1)
                return false;

            try
            {

                var salt = Convert.FromBase64String(parts[3]);
                var hash = Convert.FromBase64String(parts[4]);

                if (salt.Length == 0 || hash.Length == 0)
                    return false;

                PasswordHash = new PasswordHash(iterations, salt, hash);
                return true;

            }
            catch (FormatException)
            {
                return false;
            }

        }

        #endregion

        #region Verify(Password)

        /// <summary>
        /// Whether this is the hash of the given password.
        /// </summary>
        /// <remarks>
        /// The comparison takes the same time whether the first or the last
        /// byte differs, so that the answer says only yes or no and not how
        /// close somebody got.
        /// </remarks>
        public Boolean Verify(String? Password)
        {

            if (Password is null)
                return false;

            return CryptographicOperations.FixedTimeEquals(
                       Derive(Password, salt, Iterations, hash.Length),
                       hash
                   );

        }

        #endregion


        #region (private static) Derive(Password, Salt, Iterations, Length)

        private static Byte[] Derive(String  Password,
                                     Byte[]  Salt,
                                     Int32   Iterations,
                                     Int32   Length)

            => Rfc2898DeriveBytes.Pbkdf2(
                   Password,
                   Salt,
                   Iterations,
                   HashAlgorithmName.SHA256,
                   Length
               );

        #endregion

        #region Equals(PasswordHash) / Equals(Object) / GetHashCode()

        /// <summary>
        /// Whether both hashes were made from the same parameters, salt and password.
        /// </summary>
        public Boolean Equals(PasswordHash? Other)

            => Other is not null &&
               Iterations == Other.Iterations &&
               CryptographicOperations.FixedTimeEquals(salt, Other.salt) &&
               CryptographicOperations.FixedTimeEquals(hash, Other.hash);

        public override Boolean Equals(Object? Object)
            => Equals(Object as PasswordHash);

        public override Int32 GetHashCode()
            => HashCode.Combine(Iterations, Convert.ToBase64String(salt));

        #endregion

        #region (override) ToString()

        /// <summary>
        /// The PHC string, as it is written to the web login file.
        /// </summary>
        public override String ToString()

            => String.Concat(
                   "$",   Algorithm,
                   "$i=", Iterations.ToString(),
                   "$",   Convert.ToBase64String(salt),
                   "$",   Convert.ToBase64String(hash)
               );

        #endregion

    }

}
