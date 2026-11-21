/**
 * Where a sign-in screen is: waiting for input, sending, signed in, or refused. The card, the fuel
 * drop, the submit button and the backdrop all read it (as data-state) to show the same moment.
 */
export type SignInState = 'idle' | 'working' | 'success' | 'error';

/** How long the "signed in" moment plays before the portal opens (owner: about a second at most). */
export const SUCCESS_MS = 950;
