import { create } from 'zustand';

export type ToastTone = 'success' | 'info' | 'warning' | 'danger';

export interface ToastItem {
  id: number;
  message: string;
  tone: ToastTone;
}

interface ToastStore {
  items: ToastItem[];
  push: (message: string, tone?: ToastTone) => void;
  dismiss: (id: number) => void;
}

let nextId = 1;

export const useToastStore = create<ToastStore>((set) => ({
  items: [],
  // At most three notifications at a time.
  push: (message, tone = 'success') => set((state) => ({ items: [...state.items.slice(-2), { id: nextId++, message, tone }] })),
  dismiss: (id) => set((state) => ({ items: state.items.filter((item) => item.id !== id) })),
}));

/** Shows a short confirmation. Pass an already-translated message. */
export function toast(message: string, tone?: ToastTone): void {
  useToastStore.getState().push(message, tone);
}
