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

export type AdminUserCounts = {
  totalUsers: number;
  administrators: number;
  unconfirmedUsers: number;
  lockedUsers: number;
};

export type AdminOverview = {
  counts: AdminUserCounts & {
    connectedApplications: number;
    customScopes: number;
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

export type UserStatus = 'Confirmed' | 'Unconfirmed' | 'Locked';
export type UserSort = 'name' | 'status' | 'joined';
export type AdminUser = {
  id: string;
  name: string;
  email: string | null;
  status: UserStatus;
  createdAt: string;
  administrator: boolean;
};
export type AdminUserPage = {
  items: AdminUser[];
  totalCount: number;
  page: number;
  pageSize: number;
  asOf: string;
};
export type AdminUserDetail = {
  version: string;
  user: AdminUser;
  firstName: string;
  lastName: string;
  screenName: string;
  emailConfirmed: boolean;
  security: {
    hasPassword: boolean;
    twoFactorEnabled: boolean;
    lockoutEnabled: boolean;
    lockoutEnd: string | null;
    accessFailedCount: number;
  };
  claims: { id: number; type: string | null; value: string | null }[];
};

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

export type AdminMutationResult = { id: string; sessionEnded: boolean };
export class AdminMutationError extends AdminApiError {
  readonly messages: string[];
  constructor(status: number, messages: string[]) {
    super(status);
    this.messages = messages;
  }
}

export async function mutateAdminJson(
  path: string,
  body: unknown,
  expectedActorId: string,
  method: 'POST' | 'DELETE' = 'POST',
): Promise<AdminMutationResult> {
  // Fetch a fresh user-bound request token. The HttpOnly antiforgery cookie stays in the browser.
  const { requestToken, userId } = await getAdminJson<{ requestToken: string; userId: string }>(
    '/api/admin/antiforgery',
  );
  if (userId !== expectedActorId) throw new AdminApiError(401);
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    redirect: 'error',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': requestToken,
    },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    let messages: string[] = [];
    if (response.headers.get('Content-Type')?.includes('json')) {
      const problem = (await response.json()) as {
        title?: string;
        errors?: Record<string, string[]>;
      };
      messages = problem.errors
        ? Object.values(problem.errors)
            .flat()
            .filter((value) => typeof value === 'string')
        : [];
      if (!messages.length && problem.title && problem.title !== 'Bad Request')
        messages = [problem.title];
    }
    throw new AdminMutationError(
      response.status,
      messages.length
        ? messages
        : ['The request could not be validated. Reload the details and try again.'],
    );
  }
  if (!response.headers.get('Content-Type')?.includes('application/json'))
    throw new Error('Unexpected administration response');
  return response.json() as Promise<AdminMutationResult>;
}

export type AdminScope = {
  id: string;
  name: string | null;
  displayName: string | null;
  description: string | null;
  claims: string[];
  resources: string[];
  standard: boolean;
  version: string;
};
export type AdminScopePage = {
  items: AdminScope[];
  totalCount: number;
  page: number;
  pageSize: number;
};

export type AdminClient = {
  id: string;
  clientId: string | null;
  displayName: string | null;
  clientType: string | null;
  hasSecret: boolean;
  requirePkce: boolean;
  scopes: string[];
};
export type AdminClientDetail = {
  client: AdminClient;
  applicationType: string | null;
  consentType: string | null;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  allowRefreshTokens: boolean;
  canManage: boolean;
  version: string;
};
export type AdminClientPage = {
  items: AdminClient[];
  totalCount: number;
  page: number;
  pageSize: number;
};
