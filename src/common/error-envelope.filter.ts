import { ArgumentsHost, Catch, ExceptionFilter, HttpException, HttpStatus } from '@nestjs/common';
import type { Request, Response } from 'express';

const CODES: Record<number, string> = {
  400: 'BAD_REQUEST',
  401: 'UNAUTHORIZED',
  403: 'FORBIDDEN',
  404: 'NOT_FOUND',
  409: 'CONFLICT',
  422: 'VALIDATION_ERROR',
  429: 'RATE_LIMITED',
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
    const code = CODES[status] ?? (status >= 500 ? 'INTERNAL_ERROR' : 'ERROR');
    const message =
      exception instanceof HttpException && status !== HttpStatus.INTERNAL_SERVER_ERROR
        ? exception.message
        : 'An unexpected error occurred';

    if (status >= 500) {
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
      error: { code, message, requestId: req.requestId ?? null, details: [] },
    });
  }
}
