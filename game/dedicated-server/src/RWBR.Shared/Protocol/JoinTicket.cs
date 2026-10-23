#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace RWBR.Shared.Protocol
{
    /// <summary>
    /// Signed, single-use permission to join one match (BRD §7.2, §10 reconnect tickets).
    /// The platform API issues it over HTTPS together with the session key; the match server verifies it with the
    /// shared secret. The session key is never in the ticket: both sides derive it from the secret and ticket id.
    ///
    /// Layout (v2): u8 version | 16 ticket id | 16 match id | 16 account id | u32 expires (unix s) |
    /// u8 name length + UTF-8 name | u8 team (255 = none) | 32 HMAC-SHA256(secret, all previous bytes).
    /// Version 1 tickets (no team byte) are still accepted.
    /// </summary>
    public sealed class JoinTicket
    {
        public const byte FormatVersion = 2;
        public const byte NoTeam = 255;
        public const int SessionKeyBytes = 16;
        private const int MacBytes = 32;

        public Guid TicketId { get; private set; }
        public Guid MatchId { get; private set; }
        public Guid AccountId { get; private set; }
        public DateTimeOffset ExpiresAt { get; private set; }
        public string DisplayName { get; private set; } = "";
        /// <summary>The team the platform put this player in (Duo/Squad parties and matchmaking), or <see cref="NoTeam"/>.</summary>
        public byte Team { get; private set; } = NoTeam;

        /// <summary>Creates a ticket and the matching session key. Dev tools and tests only; production issuance is the API.</summary>
        public static byte[] Issue(byte[] secret, Guid matchId, Guid accountId, string displayName, DateTimeOffset expiresAt,
            out byte[] sessionKey, Guid? ticketId = null, byte team = NoTeam)
        {
            Guid id = ticketId ?? Guid.NewGuid();
            var w = new PacketWriter(PacketType.Hello); // reuse the little-endian writer; first byte replaced below
            w.Guid(id);
            w.Guid(matchId);
            w.Guid(accountId);
            w.U32((uint)expiresAt.ToUnixTimeSeconds());
            w.Str(displayName, ProtocolInfo.MaxNameBytes);
            w.U8(team);
            byte[] body = w.ToArray();
            body[0] = FormatVersion;

            byte[] mac;
            using (var h = new HMACSHA256(secret)) mac = h.ComputeHash(body);
            var ticket = new byte[body.Length + MacBytes];
            Buffer.BlockCopy(body, 0, ticket, 0, body.Length);
            Buffer.BlockCopy(mac, 0, ticket, body.Length, MacBytes);
            sessionKey = DeriveSessionKey(secret, id);
            return ticket;
        }

        /// <summary>Checks the signature and format. Expiry, match id and single use are checked by the caller.</summary>
        public static bool TryVerify(byte[] ticket, byte[] secret, out JoinTicket? result)
        {
            result = null;
            if (ticket == null || ticket.Length < 1 + 48 + 4 + 1 + MacBytes || (ticket[0] != FormatVersion && ticket[0] != 1)) return false;

            int bodyLength = ticket.Length - MacBytes;
            byte[] expected;
            using (var h = new HMACSHA256(secret)) expected = h.ComputeHash(ticket, 0, bodyLength);
            if (!PacketAuth.FixedTimeEquals(expected, 0, ticket, bodyLength, MacBytes)) return false;

            try
            {
                var r = new PacketReader(ticket, bodyLength); // consumes the version byte as the "type"
                result = new JoinTicket
                {
                    TicketId = r.Guid(),
                    MatchId = r.Guid(),
                    AccountId = r.Guid(),
                    ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(r.U32()),
                    DisplayName = r.Str(ProtocolInfo.MaxNameBytes),
                };
                if (ticket[0] >= 2) result.Team = r.U8();
                return true;
            }
            catch (PacketFormatException)
            {
                return false;
            }
        }

        public static byte[] DeriveSessionKey(byte[] secret, Guid ticketId)
        {
            byte[] label = Encoding.ASCII.GetBytes("rwbr-session-v1");
            byte[] input = new byte[label.Length + 16];
            Buffer.BlockCopy(label, 0, input, 0, label.Length);
            ticketId.TryWriteBytes(input.AsSpan(label.Length));
            byte[] full;
            using (var h = new HMACSHA256(secret)) full = h.ComputeHash(input);
            var key = new byte[SessionKeyBytes];
            Buffer.BlockCopy(full, 0, key, 0, SessionKeyBytes);
            return key;
        }
    }

    /// <summary>Per-packet authentication: an 8-byte truncated HMAC-SHA256 appended with the session key.</summary>
    public sealed class PacketAuth : IDisposable
    {
        public const int TagBytes = 8;
        private readonly HMACSHA256 _hmac;

        public PacketAuth(byte[] sessionKey) => _hmac = new HMACSHA256(sessionKey);

        public byte[] Sign(byte[] packet)
        {
            byte[] tag = _hmac.ComputeHash(packet);
            var signed = new byte[packet.Length + TagBytes];
            Buffer.BlockCopy(packet, 0, signed, 0, packet.Length);
            Buffer.BlockCopy(tag, 0, signed, packet.Length, TagBytes);
            return signed;
        }

        /// <summary>True when the last 8 bytes are a valid tag for the rest of the datagram.</summary>
        public bool Verify(byte[] data, int length)
        {
            if (length <= TagBytes) return false;
            byte[] tag = _hmac.ComputeHash(data, 0, length - TagBytes);
            return FixedTimeEquals(tag, 0, data, length - TagBytes, TagBytes);
        }

        public static bool FixedTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            int diff = 0;
            for (int i = 0; i < count; i++) diff |= a[aOffset + i] ^ b[bOffset + i];
            return diff == 0;
        }

        public void Dispose() => _hmac.Dispose();
    }
}
