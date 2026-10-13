import { clsx, type ClassValue } from 'clsx';

/** Joins class names. Component base classes come first; a caller's className is appended. */
export function cn(...inputs: ClassValue[]): string {
  return clsx(inputs);
}
