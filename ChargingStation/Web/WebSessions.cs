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

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.ChargingStation.Web
{

    /// <summary>
    /// One browser that got the username and the password right.
    /// </summary>
    /// <param name="Token">The random token; it only ever travels as an HttpOnly cookie.</param>
    /// <param name="Username">Who signed in.</param>
    /// <param name="CreatedAt">When they signed in.</param>
    public sealed class WebSession(SecurityToken_Id  Token,
                                   String            Username,
                                   DateTimeOffset    CreatedAt)
    {

        /// <summary>
        /// The random token; it only ever travels as an HttpOnly cookie.
        /// </summary>
        public SecurityToken_Id  Token       { get; }      = Token;

        /// <summary>
        /// Who signed in.
        /// </summary>
        public String            Username    { get; }      = Username;

        /// <summary>
        /// When they signed in.
        /// </summary>
        public DateTimeOffset    CreatedAt   { get; }      = CreatedAt;

        /// <summary>
        /// When this session was last used; the idle timeout counts from here.
        /// </summary>
        public DateTimeOffset    LastUsedAt  { get; set; } = CreatedAt;

        /// <summary>
        /// When this session ends at the latest.
        /// </summary>
        public DateTimeOffset    ExpiresAt   { get; set; } = CreatedAt;

    }


    /// <summary>
    /// Who may use the web interface: one username, one password, and a session
    /// cookie for every browser that got both right.
    /// </summary>
    /// <remarks>
    /// A charging station has one operator, and that one lives in the web login
    /// file - so what is needed of an account database is the part that was
    /// never optional: a comparison that takes the same time whether the first
    /// or the last character is wrong, a random token that only ever travels as
    /// an HttpOnly cookie, and a session that ends when nobody has used it for
    /// a while.
    ///
    /// The login is not fixed for the lifetime of the process either:
    /// <see cref="UpdateLogin"/> puts another one in force and ends every other
    /// session when it does - or a password change would not do what whoever
    /// changed it thinks it does.
    /// </remarks>
    public sealed class WebSessions
    {

        #region Data

        /// <summary>
        /// The default name of the session cookie.
        /// </summary>
        public static readonly HTTPCookieName  DefaultCookieName       = HTTPCookieName.Parse("ChargingStation");

        /// <summary>
        /// The default idle timeout: a session ends when it was not used for this long.
        /// </summary>
        public static readonly TimeSpan        DefaultIdleTimeout      = TimeSpan.FromHours(12);

        /// <summary>
        /// The default maximum lifetime of a session.
        /// </summary>
        public static readonly TimeSpan        DefaultMaximumLifetime  = TimeSpan.FromDays(7);

        /// <summary>
        /// The number of random bytes behind a session token: 256 bits, so that
        /// guessing one is not a thing anybody tries twice.
        /// </summary>
        public const           Int32           TokenBytes              = 32;

        private readonly ConcurrentDictionary<SecurityToken_Id, WebSession>  sessions = [];

        #endregion

        #region Properties

        /// <summary>
        /// The login in force: the one username and the hash of its password.
        /// </summary>
        public WebLoginSettings  Login             { get; private set; }

        /// <summary>
        /// The one username.
        /// </summary>
        public String            Username
            => Login.Username;

        /// <summary>
        /// The name of the session cookie.
        /// </summary>
        public HTTPCookieName    CookieName        { get; }

        /// <summary>
        /// Whether the cookie is marked "secure", i.e. only ever sent over TLS.
        /// </summary>
        public Boolean           SecureCookies     { get; }

        /// <summary>
        /// A session ends when it was not used for this long.
        /// </summary>
        public TimeSpan          IdleTimeout       { get; }

        /// <summary>
        /// A session ends this long after the sign-in at the latest.
        /// </summary>
        public TimeSpan          MaximumLifetime   { get; }

        /// <summary>
        /// How many sessions are live right now.
        /// </summary>
        public Int32             Count
        {
            get
            {
                RemoveExpired();
                return sessions.Count;
            }
        }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the sessions of the one user.
        /// </summary>
        /// <param name="Login">The login: the username and the hash of its password.</param>
        /// <param name="SecureCookies">Whether the cookie is only ever sent over TLS; true when the server speaks TLS.</param>
        /// <param name="CookieName">The name of the session cookie.</param>
        /// <param name="IdleTimeout">A session ends when it was not used for this long; 12 hours by default.</param>
        /// <param name="MaximumLifetime">A session ends this long after the sign-in at the latest; 7 days by default.</param>
        public WebSessions(WebLoginSettings  Login,
                           Boolean           SecureCookies     = false,
                           HTTPCookieName?   CookieName        = null,
                           TimeSpan?         IdleTimeout       = null,
                           TimeSpan?         MaximumLifetime   = null)
        {

            this.Login            = Login ?? throw new ArgumentNullException(nameof(Login));
            this.SecureCookies    = SecureCookies;
            this.CookieName       = CookieName      ?? DefaultCookieName;
            this.IdleTimeout      = IdleTimeout     ?? DefaultIdleTimeout;
            this.MaximumLifetime  = MaximumLifetime ?? DefaultMaximumLifetime;

        }

        #endregion


        #region TryLogin(Username, Password, out Session)

        /// <summary>
        /// Start a session when username and password are right.
        /// </summary>
        /// <param name="Username">What was typed as the username.</param>
        /// <param name="Password">What was typed as the password.</param>
        /// <param name="Session">The new session.</param>
        public Boolean TryLogin(String?                              Username,
                                String?                              Password,
                                [NotNullWhen(true)] out WebSession?  Session)
        {

            Session = null;

            if (!Login.Verify(Username, Password))
                return false;

            var now      = Timestamp.Now;

            // Not Random.Shared, which is what Hermod's SecurityToken_Id.Random
            // would use: this token is the whole of what stands between a
            // stranger and this charging station, so it comes out of the
            // cryptographic generator.
            var token    = SecurityToken_Id.Parse(
                               Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant()
                           );

            Session      = new WebSession(token, Login.Username, now) {
                               ExpiresAt = now + MaximumLifetime
                           };

            sessions.TryAdd(token, Session);

            RemoveExpired();

            return true;

        }

        #endregion

        #region UpdateLogin(NewLogin, ExceptToken = null)

        /// <summary>
        /// Put another login in force and end every session but the one that
        /// made the change.
        /// </summary>
        /// <remarks>
        /// Changing the password has to end the other sessions, or it does not
        /// do what whoever changed it thinks it does: a browser that was signed
        /// in with the old password would keep the page for as long as its
        /// cookie lives. The session doing the change is kept, so that the
        /// settings page does not sign itself out.
        /// </remarks>
        /// <param name="NewLogin">The login from now on.</param>
        /// <param name="ExceptToken">A session to keep, usually the one asking.</param>
        /// <returns>The number of sessions ended.</returns>
        public Int32 UpdateLogin(WebLoginSettings   NewLogin,
                                 SecurityToken_Id?  ExceptToken   = null)
        {

            Login = NewLogin ?? throw new ArgumentNullException(nameof(NewLogin));

            var ended = 0;

            foreach (var token in sessions.Keys)
            {

                if (ExceptToken.HasValue && token == ExceptToken.Value)
                    continue;

                if (sessions.TryRemove(token, out _))
                    ended++;

            }

            return ended;

        }

        #endregion

        #region TryGetSession(Request, out Session)

        /// <summary>
        /// The live session behind the request's cookie, if there is one.
        /// </summary>
        /// <remarks>
        /// Finding one is also using it: the idle timeout counts from the last
        /// request, not from the sign-in.
        /// </remarks>
        public Boolean TryGetSession(HTTPRequest                          Request,
                                     [NotNullWhen(true)] out WebSession?  Session)
        {

            Session = null;

            if (!TryGetToken(Request, out var token) ||
                !sessions.TryGetValue(token, out var session))
            {
                return false;
            }

            var now = Timestamp.Now;

            if (now > session.ExpiresAt ||
                now > session.LastUsedAt + IdleTimeout)
            {
                sessions.TryRemove(token, out _);
                return false;
            }

            session.LastUsedAt  = now;
            Session             = session;

            return true;

        }

        #endregion

        #region HasCookie(Request)

        /// <summary>
        /// Whether the request carries a session cookie at all - live or stale.
        /// </summary>
        public Boolean HasCookie(HTTPRequest Request)
            => Request.Cookies?.Contains(CookieName) == true;

        #endregion

        #region SignOut(Request)

        /// <summary>
        /// End the session behind the request's cookie.
        /// </summary>
        /// <returns>Whether there was a live session to end.</returns>
        public Boolean SignOut(HTTPRequest Request)

            => TryGetToken(Request, out var token) &&
               sessions.TryRemove(token, out _);

        #endregion


        #region SessionCookie(Session) / ExpiredCookie()

        // One HTTPCookie, parsed as one: HTTPCookies.Parse(String) is made for
        // the Cookie header of a request, where a semicolon separates cookies,
        // and would turn "Path=/" and "HttpOnly" into cookies of their own.

        /// <summary>
        /// The Set-Cookie of a sign-in: the token, HttpOnly, for this site only.
        /// </summary>
        public HTTPCookies SessionCookie(WebSession Session)

            => new (HTTPCookie.Parse(
                        String.Concat(CookieName, "=", Session.Token, CookieSettings(Session.ExpiresAt))
                    ));

        /// <summary>
        /// The Set-Cookie of a sign-out: the same cookie, expired in 1970, so
        /// that the browser drops it.
        /// </summary>
        public HTTPCookies ExpiredCookie()

            => new (HTTPCookie.Parse(
                        String.Concat(CookieName, "=", CookieSettings(DateTimeOffset.UnixEpoch))
                    ));

        #endregion


        #region (private) TryGetToken(Request, out Token)

        private Boolean TryGetToken(HTTPRequest           Request,
                                    out SecurityToken_Id  Token)
        {

            Token = default;

            return Request.Cookies is not null &&
                   Request.Cookies.TryGet(CookieName, out var cookie) &&
                   cookie?.Value is String value &&
                   SecurityToken_Id.TryParse(value, out Token);

        }

        #endregion

        #region (private) CookieSettings(Expires)

        private String CookieSettings(DateTimeOffset Expires)

            => String.Concat("; Expires=", Expires.ToRFC1123(),
                             "; Path=/",
                             "; SameSite=strict",
                             SecureCookies ? "; secure" : "",
                             "; HttpOnly");

        #endregion

        #region (private) RemoveExpired()

        /// <summary>
        /// Drop what nobody can use any more. There is no timer behind this:
        /// the store is only ever walked when somebody signs in, which is the
        /// one moment a forgotten session could otherwise start to pile up.
        /// </summary>
        private void RemoveExpired()
        {

            var now = Timestamp.Now;

            foreach (var entry in sessions)
            {
                if (now > entry.Value.ExpiresAt ||
                    now > entry.Value.LastUsedAt + IdleTimeout)
                {
                    sessions.TryRemove(entry.Key, out _);
                }
            }

        }

        #endregion

    }

}
