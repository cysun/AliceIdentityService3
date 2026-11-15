import { createContext, useContext } from 'react';
import type { AdminSession } from './admin-api';

type AdminContext = {
  session: AdminSession;
  request: <T>(
    path: string,
    signal?: AbortSignal,
    options?: { allowNotFound?: boolean },
  ) => Promise<T>;
};
export const AdminSessionContext = createContext<AdminContext | null>(null);
export function useAdminSession() {
  const context = useContext(AdminSessionContext);
  if (!context) throw new Error('Admin routes require a validated session');
  return context;
}
