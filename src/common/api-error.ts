import { HttpException } from '@nestjs/common';
import { ZodError, ZodType, ZodTypeDef } from 'zod';

export interface ErrorDetail {
  path: string;
  message: string;
}

/** An error with a stable machine-readable code for the BRD §10 envelope. */
export class ApiError extends HttpException {
  constructor(
    status: number,
    readonly code: string,
    message: string,
    readonly details: ErrorDetail[] = [],
  ) {
    super(message, status);
  }
}

export const Unauthorized = (code = 'UNAUTHORIZED', message = 'Authentication required') =>
  new ApiError(401, code, message);
export const Forbidden = (code: string, message: string) => new ApiError(403, code, message);
export const NotFound = (what: string) => new ApiError(404, 'NOT_FOUND', `${what} not found`);
export const Conflict = (code: string, message: string) => new ApiError(409, code, message);
export const Invalid = (message: string, details: ErrorDetail[] = []) =>
  new ApiError(422, 'VALIDATION_ERROR', message, details);

/** Parses input with a zod schema; failures become a 422 with per-field details (values never echoed). */
export function parse<T>(schema: ZodType<T, ZodTypeDef, unknown>, value: unknown): T {
  const result = schema.safeParse(value ?? {});
  if (result.success) return result.data;
  throw Invalid('Request validation failed', issues(result.error));
}

function issues(err: ZodError): ErrorDetail[] {
  return err.issues
    .slice(0, 20)
    .map((i) => ({ path: i.path.join('.') || '(body)', message: i.message }));
}
