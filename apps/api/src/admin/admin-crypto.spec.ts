import { randomBytes } from 'node:crypto';
import {
  base32Decode,
  base32Encode,
  decryptSecret,
  encryptSecret,
  hashPassword,
  totpAt,
  verifyPassword,
  verifyTotp,
} from './admin-crypto';

describe('admin crypto', () => {
  it('matches the RFC 6238 SHA-1 test vectors (last 6 digits)', () => {
    const secret = Buffer.from('12345678901234567890');
    const at = (seconds: number) => totpAt(secret, Math.floor(seconds / 30));
    expect(at(59)).toBe('287082');
    expect(at(1111111109)).toBe('081804');
    expect(at(1234567890)).toBe('005924');
    expect(at(20000000000)).toBe('353130');
  });

  it('accepts one step of clock drift and nothing more', () => {
    const secret = randomBytes(20);
    const now = 1_700_000_000_000;
    const step = Math.floor(now / 30_000);
    expect(verifyTotp(secret, totpAt(secret, step), now)).toBe(step);
    expect(verifyTotp(secret, totpAt(secret, step - 1), now)).toBe(step - 1);
    expect(verifyTotp(secret, totpAt(secret, step + 2), now)).toBeUndefined();
    expect(verifyTotp(secret, 'abcdef', now)).toBeUndefined();
  });

  it('round-trips base32 and encrypted secrets, and rejects tampering', () => {
    const secret = randomBytes(20);
    expect(base32Decode(base32Encode(secret))).toEqual(secret);
    const key = 'c3'.repeat(32);
    const blob = encryptSecret(key, secret);
    expect(decryptSecret(key, blob)).toEqual(secret);
    blob[blob.length - 1]! ^= 1;
    expect(() => decryptSecret(key, blob)).toThrow();
  });

  it('hashes passwords with scrypt and verifies them', async () => {
    const hash = await hashPassword('a long admin password');
    expect(hash).toMatch(/^scrypt\$32768\$8\$1\$/);
    expect(await verifyPassword('a long admin password', hash)).toBe(true);
    expect(await verifyPassword('wrong', hash)).toBe(false);
  });
});
