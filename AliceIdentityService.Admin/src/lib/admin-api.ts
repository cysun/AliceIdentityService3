export type AdminSession = {
  user: {
    id: string;
    email: string | null;
    firstName: string;
    lastName: string;
    screenName: string;
  };
  expiresAt: string;
  serverTime: string;
  application: { name: string; version: string; implementationStep: number };
};

export type AdminOverview = {
  counts: {
    totalUsers: number;
    connectedApplications: number;
    unconfirmedUsers: number;
    administrators: number;
    lockedUsers: number;
  };
  recentRegistrations: {
    id: string;
    name: string;
    email: string | null;
    status: 'Confirmed' | 'Unconfirmed' | 'Locked';
    createdAt: string;
  }[];
  applications: { id: string; name: string; clientId: string | null }[];
  asOf: string;
};

export class AdminApiError extends Error {
  readonly status: number;
  constructor(status: number) {
    super('Administration request failed');
    this.status = status;
  }
}

export async function getAdminJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const timeout = AbortSignal.timeout(10_000);
  const response = await fetch(path, {
    credentials: 'same-origin',
    cache: 'no-store',
    redirect: 'error',
    headers: { Accept: 'application/json' },
    signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
  });
  if (!response.ok) throw new AdminApiError(response.status);
  if (!response.headers.get('Content-Type')?.includes('application/json'))
    throw new Error('Unexpected administration response');
  return response.json() as Promise<T>;
}
