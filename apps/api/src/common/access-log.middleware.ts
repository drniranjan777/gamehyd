import type { NextFunction, Response } from 'express';
import type { AuthedRequest } from './guards';

/**
 * One JSON line per request: method, path (no query string), status, duration, request and user IDs.
 * Headers, bodies, query strings and tokens are never logged (BRD §10 "redact sensitive fields").
 */
export function accessLog(enabled: boolean) {
  return (req: AuthedRequest, res: Response, next: NextFunction): void => {
    if (!enabled) return next();
    const start = process.hrtime.bigint();
    res.on('finish', () => {
      const ms = Number(process.hrtime.bigint() - start) / 1e6;
      console.log(
        JSON.stringify({
          level: res.statusCode >= 500 ? 'error' : 'info',
          msg: 'request',
          requestId: req.requestId,
          method: req.method,
          path: req.path,
          status: res.statusCode,
          ms: Math.round(ms * 10) / 10,
          userId: req.userId,
          server: req.serverAddress,
        }),
      );
    });
    next();
  };
}
