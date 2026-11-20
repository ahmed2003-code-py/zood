import { apiRequest } from '@/api/http-client';
import type { LoginResult, MfaEnrollment, Session, SessionUser } from './types';

/** Every endpoint that reads the refresh cookie also requires this header (CSRF defence). */
const CLIENT_HEADER = { 'X-Zawed-Client': 'web' } as const;

/**
 * Sign-in and session endpoints. Paths and shapes follow docs/architecture/b1-identity-contract.md.
 * One sign-in path for every role, the Super Admin included (ADR 0008 amendment, 2026-11-20).
 */
export const authApi = {
  login: (email: string, password: string) =>
    apiRequest<LoginResult>('/auth/login', { method: 'POST', anonymous: true, json: { email, password } }),

  verifyMfa: (challengeToken: string, code: string) =>
    apiRequest<LoginResult>('/auth/mfa/verify', { method: 'POST', anonymous: true, json: { challengeToken, code } }),

  startEnrollment: (challengeToken: string) =>
    apiRequest<MfaEnrollment>('/auth/mfa/enroll/start', { method: 'POST', anonymous: true, json: { challengeToken } }),

  confirmEnrollment: (challengeToken: string, code: string) =>
    apiRequest<LoginResult>('/auth/mfa/enroll/confirm', { method: 'POST', anonymous: true, json: { challengeToken, code } }),

  refresh: () => apiRequest<Session>('/auth/refresh', { method: 'POST', anonymous: true, headers: CLIENT_HEADER }),

  logout: () => apiRequest<void>('/auth/logout', { method: 'POST', anonymous: true, headers: CLIENT_HEADER }),

  me: () => apiRequest<SessionUser>('/auth/me'),

  changePassword: (currentPassword: string, newPassword: string) =>
    apiRequest<void>('/auth/password', { method: 'POST', json: { currentPassword, newPassword } }),
};
