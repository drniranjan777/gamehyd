import { createHmac, randomUUID, timingSafeEqual } from 'node:crypto';

/**
 * Join tickets, byte-compatible with RWBR.Shared.Protocol.JoinTicket (ADR 0007). The match server verifies
 * them with the same secret and derives the same session key; the key itself never travels in the ticket.
 */
export const TICKET_FORMAT_VERSION = 1;
const MAX_NAME_BYTES = 24;
const MAC_BYTES = 32;

/** .NET Guid byte order: first three groups little-endian, last two as written. */
export function guidToBytes(uuid: string): Buffer {
  const hex = uuid.replace(/-/g, '');
  if (!/^[0-9a-fA-F]{32}$/.test(hex)) throw new Error('not a uuid');
  const b = Buffer.from(hex, 'hex');
  return Buffer.concat([
    Buffer.from(b.subarray(0, 4)).reverse(),
    Buffer.from(b.subarray(4, 6)).reverse(),
    Buffer.from(b.subarray(6, 8)).reverse(),
    b.subarray(8, 16),
  ]);
}

export function bytesToGuid(b: Buffer): string {
  const h = Buffer.concat([
    Buffer.from(b.subarray(0, 4)).reverse(),
    Buffer.from(b.subarray(4, 6)).reverse(),
    Buffer.from(b.subarray(6, 8)).reverse(),
    b.subarray(8, 16),
  ]).toString('hex');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export function deriveSessionKey(secret: Buffer, ticketId: string): Buffer {
  return createHmac('sha256', secret)
    .update(Buffer.concat([Buffer.from('rwbr-session-v1', 'ascii'), guidToBytes(ticketId)]))
    .digest()
    .subarray(0, 16);
}

export interface IssuedTicket {
  ticket: Buffer;
  sessionKey: Buffer;
  ticketId: string;
}

export function issueTicket(
  secret: Buffer,
  matchId: string,
  accountId: string,
  displayName: string,
  expiresAt: Date,
  ticketId: string = randomUUID(),
): IssuedTicket {
  const name = Buffer.from(displayName, 'utf8').subarray(0, MAX_NAME_BYTES);
  const expires = Buffer.alloc(4);
  expires.writeUInt32LE(Math.floor(expiresAt.getTime() / 1000));
  const body = Buffer.concat([
    Buffer.from([TICKET_FORMAT_VERSION]),
    guidToBytes(ticketId),
    guidToBytes(matchId),
    guidToBytes(accountId),
    expires,
    Buffer.from([name.length]),
    name,
  ]);
  const mac = createHmac('sha256', secret).update(body).digest();
  return {
    ticket: Buffer.concat([body, mac]),
    sessionKey: deriveSessionKey(secret, ticketId),
    ticketId,
  };
}

export interface ParsedTicket {
  ticketId: string;
  matchId: string;
  accountId: string;
  expiresAt: Date;
  displayName: string;
}

/** Signature and format check (tests and tooling; the match server does the real verification). */
export function verifyTicket(ticket: Buffer, secret: Buffer): ParsedTicket | null {
  if (ticket.length < 1 + 48 + 4 + 1 + MAC_BYTES || ticket[0] !== TICKET_FORMAT_VERSION)
    return null;
  const body = ticket.subarray(0, ticket.length - MAC_BYTES);
  const mac = createHmac('sha256', secret).update(body).digest();
  if (!timingSafeEqual(mac, ticket.subarray(ticket.length - MAC_BYTES))) return null;
  const nameLength = body[53]!;
  if (body.length !== 54 + nameLength) return null;
  return {
    ticketId: bytesToGuid(body.subarray(1, 17)),
    matchId: bytesToGuid(body.subarray(17, 33)),
    accountId: bytesToGuid(body.subarray(33, 49)),
    expiresAt: new Date(body.readUInt32LE(49) * 1000),
    displayName: body.subarray(54).toString('utf8'),
  };
}
