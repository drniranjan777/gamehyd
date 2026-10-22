import { ArgumentsHost, Catch, ExceptionFilter, HttpException, HttpStatus } from '@nestjs/common';
import type { Request, Response } from 'express';
import { ApiError } from './api-error';

const CODES: Record<number, string> = {
  400: 'BAD_REQUEST',
  401: 'UNAUTHORIZED',
  403: 'FORBIDDEN',
  404: 'NOT_FOUND',
  409: 'CONFLICT',
  413: 'PAYLOAD_TOO_LARGE',
  422: 'VALIDATION_ERROR',
  423: 'LOCKED',
  429: 'RATE_LIMITED',
  501: 'NOT_IMPLEMENTED',
  503: 'SERVICE_UNAVAILABLE',
};

/** BRD §10 error envelope. Never returns stack traces or internal messages for 5xx. */
@Catch()
export class ErrorEnvelopeFilter implements ExceptionFilter {
  catch(exception: unknown, host: ArgumentsHost): void {
    const ctx = host.switchToHttp();
    const req = ctx.getRequest<Request & { requestId?: string }>();
    const res = ctx.getResponse<Response>();

    const status =
      exception instanceof HttpException ? exception.getStatus() : HttpStatus.INTERNAL_SERVER_ERROR;
    const code =
      exception instanceof ApiError
        ? exception.code
        : (CODES[status] ?? (status >= 500 ? 'INTERNAL_ERROR' : 'ERROR'));
    const message =
      exception instanceof HttpException && status !== HttpStatus.INTERNAL_SERVER_ERROR
        ? exception.message
        : 'An unexpected error occurred';
    const details = exception instanceof ApiError ? exception.details : [];

    // Deliberate 5xx (kill switches, platform not configured) carry an ApiError code and are not failures.
    if (status >= 500 && !(exception instanceof ApiError)) {
      console.error(
        JSON.stringify({
          level: 'error',
          msg: 'request failed',
          requestId: req.requestId,
          path: req.path,
          error: exception instanceof Error ? exception.message : String(exception),
        }),
      );
    }

    res.status(status).json({
      error: { code, message, requestId: req.requestId ?? null, details },
    });
  }
}
