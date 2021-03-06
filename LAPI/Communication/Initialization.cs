﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using LAPI.Contracts;
using LAPI.Extensions;
using LAPI.Model;
using LAPI.Model.Cryptography;
using LAPI.Model.Exceptions;

namespace LAPI.Communication
{
    public class Initialization
    {
        private static CommunicationResult<TResult> From<TResult>(CommunicationResult result) =>
            CommunicationResult<TResult>.From(result);
        public static async Task<CommunicationResult> HandleInitializationOfClient(
            Stream clientStream,
            byte[] presharedKey,
            Guid serverGuid,
            X509Certificate2 serverCertificate,
            ICryptographicService otp,
            Action<AuthenticatedStream> onClientConnected,
            Action<Guid, X509Certificate> onClientRegistered,
            Func<Guid, X509Certificate> getClientPublicKey,
            CancellationToken token)
        {
            var timeoutCancellationTokenSource = new CancellationTokenSource();
            token.Register(timeoutCancellationTokenSource.Cancel);
            if (token.IsCancellationRequested)
            {
                return CommunicationResult.Failed;
            }
            timeoutCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(5));
            token = timeoutCancellationTokenSource.Token;
            if (!await GetAndVerifyPresharedKey(clientStream, presharedKey, token))
            {
                return CommunicationResult.Failed;
            }
            await clientStream.WriteSafelyAsync(token, serverGuid);
            var clientGuidResult = await clientStream.ReceiveGuidSafelyAsync(token);
            if (!clientGuidResult.Successful)
            {
                return clientGuidResult;
            }
            var clientGuid = clientGuidResult.Result;
            var initiationModeResult = await clientStream.ReceiveInt32SafelyAsync(token);
            if (!initiationModeResult.Successful)
            {
                return initiationModeResult;
            }
            switch ((InitiationMode) initiationModeResult.Result)
            {
                case InitiationMode.Otp:
                    var clientRegistrationResult =
                        await HandleClientRegistrationSafelyAsync(clientStream, otp, token);
                    if (clientRegistrationResult.Successful)
                    {
                        onClientRegistered(clientGuid, clientRegistrationResult.Result);
                    }
                    else
                    {
                        return clientRegistrationResult;
                    }
                    return new CommunicationResult
                    {
                        Successful = true,
                    };
                case InitiationMode.Standard:
                    var clientCertificate = getClientPublicKey(clientGuid);
                    if (clientCertificate == null)
                    {
                        return CommunicationResult.Failed;
                    }
                    //var sessionKey = SymmetricKey.GenerateNewKey(symmetric.KeyLength);

                    var encryptedStreamResult = await EstablishEncryptedCommunication(true, serverGuid, serverCertificate, clientCertificate, clientStream);
                    if (!encryptedStreamResult.Successful)
                    {
                        return encryptedStreamResult;
                    }
                    onClientConnected(encryptedStreamResult.Result);
                    return new CommunicationResult
                    {
                        Successful = true,
                    };
                default:
                    throw new ProtocolException($"invalid initiation mode {initiationModeResult.Result}");
            }
        }

        public static async Task<bool> GetAndVerifyPresharedKey(
            Stream clientStream, 
            byte[] presharedKey,
            CancellationToken token)
        {
            var result = await clientStream.ReadSafelyAsync(presharedKey.Length, token);
            if (result.Successful)
            {
                var clientPsk = result.Result;
                if (clientPsk.Length != presharedKey.Length)
                {
                    return false;
                }
                return clientPsk.StartsWith(presharedKey);
            }
            return false;
        }

        public static async Task<CommunicationResult<X509Certificate>> HandleClientRegistrationSafelyAsync(
            Stream clientStream,
            ICryptographicService otp, 
            CancellationToken token)
        {
            CommunicationResult<X509Certificate> From(CommunicationResult result) => From<X509Certificate>(result);
            var Failed = From(CommunicationResult.Failed);

            if (!otp.CanDecrypt)
            {
                throw new ArgumentException("otp needs to be able to decrypt");
            }

            var dataTypeResult = await clientStream.ReceiveInt32SafelyAsync(token);
            if (!dataTypeResult.Successful)
            {
                return From(dataTypeResult);
            }
            if ((CommunicationData) dataTypeResult.Result != CommunicationData.PublicKey)
            {
                return Failed;
            }

            var getKeyLengthResult = await GetEncryptedPublicKeyLengths(clientStream, token);
            if (!getKeyLengthResult.Successful)
            {
                return From(getKeyLengthResult);
            }
            var pubKeyInfo = getKeyLengthResult.Result;
            var encryptedCombinationResult =
                await clientStream.ReadSafelyAsync(pubKeyInfo.EncryptedCombinationLength, token);
            if (!encryptedCombinationResult.Successful)
            {
                return From(encryptedCombinationResult);
            }
            var encryptedCombination = encryptedCombinationResult.Result;
            var decryptedCombination = otp.Decrypt(encryptedCombination);
            var modulus = new byte[pubKeyInfo.DecryptedModulusLength];
            var decryptedExponentLength = decryptedCombination.Length - pubKeyInfo.DecryptedModulusLength - pubKeyInfo.ExponentPadding;
            var exponent = new byte[decryptedExponentLength];
            Array.Copy(decryptedCombination, 0, modulus, 0, pubKeyInfo.DecryptedModulusLength);
            Array.Copy(decryptedCombination, pubKeyInfo.DecryptedModulusLength, exponent, 0, decryptedExponentLength);
            var clientPublicKey = new RsaPublicKey(modulus, exponent);
            return new CommunicationResult<X509Certificate>
            {
                Successful = true,
                Result = null,//clientPublicKey,
            };
        }

        public static async Task<CommunicationResult<EncryptedPublicKeyInformationMessage>> GetEncryptedPublicKeyLengths(
            Stream clientStream, 
            CancellationToken token)
        {
            var result = await clientStream.ReadSafelyAsync(2 * Size.Int + Size.Byte, token);
            if (!result.Successful)
            {
                return From<EncryptedPublicKeyInformationMessage>(result);
            }
            var buffer = result.Result;
            var first = BitConverter.ToInt32(buffer, 0);
            var second = BitConverter.ToInt32(buffer, Size.Int);
            var third = buffer[2 * Size.Int];
            return new CommunicationResult<EncryptedPublicKeyInformationMessage>()
            {
                Successful = true,
                Result = new EncryptedPublicKeyInformationMessage(first, second, third),
            };
        }

        public static async Task<CommunicationResult> HandleServerDiscoveryMessage(
            byte[] request,
            IPEndPoint remoteEndPoint,
            byte[] presharedKey,
            Guid serverGuid,
            CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return CommunicationResult.Failed;
            }
            if (!request.StartsWith(presharedKey))
            {
                return CommunicationResult.Failed;
            }
            var client = new UdpClient(remoteEndPoint.Port, remoteEndPoint.AddressFamily);
            var datagram = serverGuid.ToByteArray();
            try
            {
                await client.SendAsync(datagram, datagram.Length, remoteEndPoint);
            }
            catch (Exception exc)
            {
                return new CommunicationResult()
                {
                    Successful = false,
                    Exception = exc,
                };
            }
            return new CommunicationResult()
            {
                Successful = true,
            };
        }

        public static async Task<CommunicationResult<AuthenticatedStream>> EstablishEncryptedCommunication(
            bool asServer,
            Guid serverGuid,
            X509Certificate2 ownCertificate,
            X509Certificate otherCertificate,
            Stream stream)
        {
            bool IsValidRemoteParty(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
            {
                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    return false;
                }

                return true; //TODO
            }
            SslStream sslStream = new SslStream(stream, false, IsValidRemoteParty);
            try
            {
                if (asServer)
                {
                    await sslStream.AuthenticateAsServerAsync(ownCertificate, true, SslProtocols.None, true);
                }
                else
                {
                    var certs = new X509CertificateCollection() { ownCertificate };
                    await sslStream.AuthenticateAsClientAsync(serverGuid.ToString(), certs, SslProtocols.None, true);
                }
            }
            catch (AuthenticationException authExc)
            {
                return new CommunicationResult<AuthenticatedStream>
                {
                    Successful = false,
                    Exception = authExc,
                };
            }

            return new CommunicationResult<AuthenticatedStream>
            {
                Successful = true,
                Result = sslStream,
            };
        }

        public static async Task<CommunicationResult<AuthenticatedStream>> ConnectToServer(
            Stream serverStream,
            byte[] presharedKey,
            Guid ownGuid,
            Guid serverGuid,
            X509Certificate2 clientCertificate,
            X509Certificate serverCertificate)
        {
            CommunicationResult<AuthenticatedStream> From(CommunicationResult res) =>
                CommunicationResult<AuthenticatedStream>.From(res);
            var timeoutCancellationTokenSource = new CancellationTokenSource();
            timeoutCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(5));
            var token = timeoutCancellationTokenSource.Token;

            await serverStream.WriteSafelyAsync(token, presharedKey);
            var serverGuidResult = await serverStream.ReceiveGuidSafelyAsync(token);
            if (!serverGuidResult.Successful)
            {
                return From(serverGuidResult);
            }
            await serverStream.WriteSafelyAsync(token, ownGuid, (int) InitiationMode.Standard);

            var encryptedStreamResult = await EstablishEncryptedCommunication(false, serverGuid, clientCertificate,
                serverCertificate, serverStream);
            return encryptedStreamResult;
        }
    }
}