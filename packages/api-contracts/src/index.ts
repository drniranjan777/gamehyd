// Types generated from openapi.yaml (run `pnpm --filter @rwbr/api-contracts generate` after editing the spec).
import type { components, paths } from './schema';

export type { components, paths };
export type Schemas = components['schemas'];

export type AuthResponse = Schemas['AuthResponse'];
export type Profile = Schemas['Profile'];
export type Stats = Schemas['Stats'];
export type MatchHistoryPage = Schemas['MatchHistoryPage'];
export type QueueStatus = Schemas['QueueStatus'];
export type Seat = Schemas['Seat'];
export type Inventory = Schemas['Inventory'];
export type Report = Schemas['Report'];
export type HeartbeatRequest = Schemas['HeartbeatRequest'];
export type HeartbeatResponse = Schemas['HeartbeatResponse'];
export type ResultSubmission = Schemas['ResultSubmission'];
export type MatchResult = Schemas['MatchResult'];
export type ErrorEnvelope = Schemas['ErrorEnvelope'];

/** Path of the spec, for contract tests that validate responses against it. */
export const openApiPath = require.resolve('../openapi.yaml');
