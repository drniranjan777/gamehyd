import { Body, Controller, Headers, HttpCode, Post, UseGuards } from '@nestjs/common';
import { z } from 'zod';
import { parse } from '../common/api-error';
import { RateLimit, RateLimitGuard } from '../common/guards';
import { displayName } from '../common/schemas';
import { AuthResult, AuthService } from './auth.service';

const guestBody = z
  .object({
    deviceId: z.string().uuid().optional(),
    deviceSecret: z
      .string()
      .regex(/^[A-Za-z0-9_-]{43}$/)
      .optional(),
    displayName: displayName.optional(),
  })
  .strict();
const platformBody = z
  .object({ provider: z.enum(['GOOGLE_PLAY', 'APPLE']), token: z.string().min(10).max(4096) })
  .strict();
const refreshBody = z.object({ refreshToken: z.string().length(43) }).strict();

@Controller('auth')
@UseGuards(RateLimitGuard)
export class AuthController {
  constructor(private readonly auth: AuthService) {}

  @Post('guest')
  @HttpCode(200)
  @RateLimit({ limit: 20, windowSeconds: 60, key: 'ip' })
  guest(@Body() body: unknown, @Headers('user-agent') ua?: string): Promise<AuthResult> {
    return this.auth.guest(parse(guestBody, body), ua);
  }

  @Post('platform')
  @HttpCode(200)
  @RateLimit({ limit: 20, windowSeconds: 60, key: 'ip' })
  platform(@Body() body: unknown): never {
    parse(platformBody, body);
    return this.auth.platform();
  }

  @Post('token/refresh')
  @HttpCode(200)
  @RateLimit({ limit: 60, windowSeconds: 60, key: 'ip' })
  refresh(@Body() body: unknown, @Headers('user-agent') ua?: string): Promise<AuthResult> {
    return this.auth.refresh(parse(refreshBody, body).refreshToken, ua);
  }

  @Post('logout')
  @HttpCode(204)
  async logout(@Body() body: unknown): Promise<void> {
    await this.auth.logout(parse(refreshBody, body).refreshToken);
  }
}
