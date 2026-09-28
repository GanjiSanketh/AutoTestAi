import { UserManager, WebStorageStateStore } from 'oidc-client-ts';

const authority = import.meta.env.VITE_KEYCLOAK_URL
  ? `${import.meta.env.VITE_KEYCLOAK_URL}/realms/${import.meta.env.VITE_KEYCLOAK_REALM ?? 'autotestai'}`
  : '';
const clientId = import.meta.env.VITE_KEYCLOAK_CLIENT_ID ?? 'autotestai-web';

/**
 * Standard OIDC code-flow client (Keycloak). Tokens live in sessionStorage —
 * never in localStorage, never in Zustand, never in logs.
 */
export const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: `${window.location.origin}/callback`,
  silent_redirect_uri: `${window.location.origin}/silent-renew`,
  post_logout_redirect_uri: `${window.location.origin}/login`,
  response_type: 'code',
  scope: 'openid profile email',
  automaticSilentRenew: true,
  monitorSession: false,
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
});

export interface SignInState {
  returnUrl?: string;
}

export async function getAccessToken(): Promise<string | null> {
  const user = await userManager.getUser();
  if (!user || user.expired) return null;
  return user.access_token;
}
