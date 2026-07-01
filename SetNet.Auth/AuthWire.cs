using System;
using System.IO;
using System.Text;

namespace SetNet.Auth
{
    /// <summary>
    /// Hand-framed auth request. Encoded as a plain <c>byte[]</c> so it rides over any configured serializer with
    /// no type attributes (RPC uses the same trick). Sent under <see cref="AuthTypes.Request"/>.
    /// </summary>
    internal readonly struct AuthRequest
    {
        public readonly int CorrelationId;
        public readonly AuthKind Kind;
        public readonly string Token;

        public AuthRequest(int correlationId, AuthKind kind, string token)
        {
            CorrelationId = correlationId;
            Kind = kind;
            Token = token ?? "";
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId);
                w.Write((byte)Kind);
                w.Write(Token);
            }
            return ms.ToArray();
        }

        public static AuthRequest Decode(byte[] frame)
        {
            try
            {
                using var ms = new MemoryStream(frame);
                using var r = new BinaryReader(ms, Encoding.UTF8);
                var corr = r.ReadInt32();
                var kind = (AuthKind)r.ReadByte();
                var token = r.ReadString();
                return new AuthRequest(corr, kind, token);
            }
            catch (Exception ex)
            {
                throw new AuthException("Malformed auth request: " + ex.Message);
            }
        }
    }

    /// <summary>Hand-framed auth response, sent under <see cref="AuthTypes.Response"/>.</summary>
    internal readonly struct AuthResponse
    {
        public readonly int CorrelationId;
        public readonly bool Success;
        public readonly string AccountId;
        public readonly string SessionId;
        public readonly string ReconnectToken;
        public readonly string Error;

        public AuthResponse(int correlationId, bool success, string accountId, string sessionId, string reconnectToken, string error)
        {
            CorrelationId = correlationId;
            Success = success;
            AccountId = accountId ?? "";
            SessionId = sessionId ?? "";
            ReconnectToken = reconnectToken ?? "";
            Error = error ?? "";
        }

        public static AuthResponse Ok(int corr, string accountId, string sessionId, string reconnectToken)
            => new AuthResponse(corr, true, accountId, sessionId, reconnectToken, "");

        public static AuthResponse Fail(int corr, string error)
            => new AuthResponse(corr, false, "", "", "", error);

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId);
                w.Write(Success);
                if (Success)
                {
                    w.Write(AccountId);
                    w.Write(SessionId);
                    w.Write(ReconnectToken);
                }
                else
                {
                    w.Write(Error);
                }
            }
            return ms.ToArray();
        }

        public static AuthResponse Decode(byte[] frame)
        {
            try
            {
                using var ms = new MemoryStream(frame);
                using var r = new BinaryReader(ms, Encoding.UTF8);
                var corr = r.ReadInt32();
                var success = r.ReadBoolean();
                if (success)
                    return Ok(corr, r.ReadString(), r.ReadString(), r.ReadString());
                return Fail(corr, r.ReadString());
            }
            catch (Exception ex)
            {
                throw new AuthException("Malformed auth response: " + ex.Message);
            }
        }
    }
}
