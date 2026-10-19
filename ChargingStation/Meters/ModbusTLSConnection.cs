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

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// One Modbus/TLS connection to a meter: TCP, the TLS handshake with this
    /// station's certificate shown and the meter's judged, then one request at
    /// a time.
    /// </summary>
    /// <remarks>
    /// The station's own rather than Hermod's client, which believes the meter
    /// against one CA it is handed and says nothing of the handshake: here the
    /// meter is believed as the node believes every server, against the roots
    /// of its store, and the page says over what it is read. The frames and
    /// the requests in them are Hermod's.
    /// </remarks>
    internal sealed class ModbusTLSConnection : IAsyncDisposable
    {

        #region Data

        private readonly TcpClient      tcp;
        private readonly SslStream      tls;
        private readonly SemaphoreSlim  exchange = new (1, 1);
        private          UInt16         transactionId;

        #endregion

        #region Properties

        /// <summary>
        /// The TLS version agreed on.
        /// </summary>
        public SslProtocols         Protocol            { get; }

        /// <summary>
        /// The cipher suite agreed on.
        /// </summary>
        public String               CipherSuite         { get; }

        /// <summary>
        /// The certificate the meter showed.
        /// </summary>
        public X509Certificate2?    MeterCertificate    { get; }

        #endregion

        #region Constructor(s)

        private ModbusTLSConnection(TcpClient  TCP,
                                    SslStream  TLS)
        {

            this.tcp               = TCP;
            this.tls               = TLS;
            this.Protocol          = TLS.SslProtocol;
            this.CipherSuite       = TLS.NegotiatedCipherSuite.ToString();
            // A copy of its own: the stream's goes with the stream.
            this.MeterCertificate  = TLS.RemoteCertificate is X509Certificate remote
                                         ? X509CertificateLoader.LoadCertificate(remote.GetRawCertData())
                                         : null;

        }

        #endregion


        #region (static) Open(Host, Port, Identity, Intermediates, Judge, Timeout, CancellationToken)

        /// <summary>
        /// Connect to a meter and agree on TLS with it.
        /// </summary>
        /// <param name="Host">Where it is, and what its certificate has to be issued for.</param>
        /// <param name="Port">Its port.</param>
        /// <param name="Identity">Who this station is to it, with its private key.</param>
        /// <param name="Intermediates">The CAs between that certificate and its root, sent along: SunSpec wants the whole chain from a peer.</param>
        /// <param name="Judge">Whether to go ahead with the certificate the meter showed.</param>
        /// <param name="Timeout">How long the connection and the handshake may take, each.</param>
        /// <param name="CancellationToken">To give up.</param>
        public static async Task<ModbusTLSConnection> Open(String                               Host,
                                                           UInt16                               Port,
                                                           X509Certificate2                     Identity,
                                                           IReadOnlyCollection<X509Certificate2> Intermediates,
                                                           RemoteCertificateValidationCallback  Judge,
                                                           TimeSpan                             Timeout,
                                                           CancellationToken                    CancellationToken)
        {

            var tcp = new TcpClient { NoDelay = true };

            try
            {

                using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken))
                {
                    connecting.CancelAfter(Timeout);
                    await tcp.ConnectAsync(Host, Port, connecting.Token);
                }

                var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

                try
                {

                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
                    handshake.CancelAfter(Timeout);

                    await tls.AuthenticateAsClientAsync(
                              new SslClientAuthenticationOptions {
                                  TargetHost                      = Host,
                                  ClientCertificateContext        = SslStreamCertificateContext.Create(Identity, [.. Intermediates], offline: true),
                                  EnabledSslProtocols             = SslProtocols.Tls12 | SslProtocols.Tls13,
                                  CertificateRevocationCheckMode  = X509RevocationMode.NoCheck,
                                  AllowRenegotiation              = false,
                                  EncryptionPolicy                = EncryptionPolicy.RequireEncryption,
                                  RemoteCertificateValidationCallback = Judge
                              },
                              handshake.Token
                          );

                    return new ModbusTLSConnection(tcp, tls);

                }
                catch
                {
                    await tls.DisposeAsync();
                    throw;
                }

            }
            catch
            {
                tcp.Dispose();
                throw;
            }

        }

        #endregion

        #region ReadHoldingRegisters(Unit, First, Count, Timeout, CancellationToken)

        /// <summary>
        /// Read a block of holding registers.
        /// </summary>
        /// <exception cref="ModbusException">Where the meter refused, with its reason.</exception>
        public async Task<UInt16[]> ReadHoldingRegisters(Byte               Unit,
                                                         UInt16             First,
                                                         UInt16             Count,
                                                         TimeSpan           Timeout,
                                                         CancellationToken  CancellationToken)
        {

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            waiting.CancelAfter(Timeout);

            await exchange.WaitAsync(waiting.Token);

            try
            {

                var id       = unchecked(++transactionId);
                var request  = new ModbusFrame(id, Unit, ModbusPDU.BuildReadHoldingRegisters(First, Count));

                await tls.WriteAsync(request.ToBytes(), waiting.Token);
                await tls.FlushAsync(waiting.Token);

                var response = await ModbusFrame.ReadAsync(tls, waiting.Token);

                if (response.TransactionId != id || response.UnitId != Unit)
                    throw new InvalidDataException($"The meter answered transaction {response.TransactionId} of unit {response.UnitId}, where {id} of unit {Unit} was asked.");

                return ModbusPDU.DecodeReadResponse(response.Pdu);

            }
            finally
            {
                exchange.Release();
            }

        }

        #endregion


        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {

            try { await tls.DisposeAsync(); } catch { }

            tcp.Dispose();
            MeterCertificate?.Dispose();
            exchange.Dispose();

        }

        #endregion

    }

}
