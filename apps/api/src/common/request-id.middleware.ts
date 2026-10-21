import { randomUUID } from 'node:crypto';
import type { NextFunction, Request, Response } from 'express';

const VALID_ID = /^[A-Za-z0-9-]{8,64}$/;

/** Attaches a request ID (client-supplied if well formed, else generated) to req and the response. */
export function requestId(req: Request, res: Response, next: NextFunction): void {
  const incoming = req.header('x-request-id');
  const id = incoming && VALID_ID.test(incoming) ? incoming : randomUUID();
  (req as Request & { requestId: string }).requestId = id;
  res.setHeader('x-request-id', id);
  next();
}
