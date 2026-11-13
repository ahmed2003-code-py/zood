/**
 * Identity types, taken from the API's OpenAPI description (src/api/generated). A change on the
 * server that the web has not caught up with fails the typecheck here, not at runtime.
 */
import type { components } from '@/api/generated/schema';

type Schemas = components['schemas'];

export type Role = Schemas['UserResponse']['role'];
export type Scope = Schemas['ScopeResponse'];
export type SessionUser = Schemas['UserResponse'];
export type Session = Schemas['SessionResponse'];
export type MfaEnrollment = Schemas['EnrollmentResponse'];

/**
 * The sign-in result, narrowed by `status` so each branch has the fields it needs. The API sends
 * one object with optional fields; the assertion below keeps the two in step.
 */
export type LoginResult =
  | { status: 'AUTHENTICATED'; session: Session }
  | { status: 'MFA_REQUIRED'; challengeToken: string; expiresInSeconds: number }
  | { status: 'MFA_ENROLLMENT_REQUIRED'; challengeToken: string; expiresInSeconds: number };

// Compile-time check: every branch is a valid LoginResponse from the API.
type AssertAssignable<T extends Schemas['LoginResponse']> = T;
export type LoginResultMatchesApi = AssertAssignable<LoginResult>;

/**
 * Which sign-in door a session came through. Super Admins have their own door and their own
 * refresh cookie (PRD §6.4: a separate authentication domain).
 */
export type Door = 'user' | 'admin';
