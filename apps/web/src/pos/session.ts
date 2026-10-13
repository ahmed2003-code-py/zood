import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { Id } from '@/domain/types';

interface PosSession {
  agentId: Id | null;
  signIn: (agentId: Id) => void;
  signOut: () => void;
}

/**
 * The signed-in agent on this POS window. Per browser tab (sessionStorage), like a device session.
 * Production: Worker ID + PIN verified offline against a hashed credential store (PRD §6.1).
 */
export const usePosSession = create<PosSession>()(
  persist(
    (set) => ({
      agentId: null,
      signIn: (agentId) => set({ agentId }),
      signOut: () => set({ agentId: null }),
    }),
    { name: 'zood.pos.session', storage: createJSONStorage(() => sessionStorage) },
  ),
);
