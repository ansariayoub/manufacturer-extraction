import {
  BrowserAuthError,
  createStandardPublicClientApplication,
  InteractionRequiredAuthError,
  type IPublicClientApplication,
} from '@azure/msal-browser';

const clientId: string = import.meta.env.VITE_AZURE_CLIENT_ID ?? '';
const tenantId: string = import.meta.env.VITE_AZURE_TENANT_ID ?? '';

/**
 * Sign-in is only enforced when the Azure app registration's ids are configured at build time.
 * Without them (local development, or before the registration exists) the app runs open exactly as
 * it did before sign-in was added — matching the backend, which stays open under the same condition.
 */
export const authEnabled = Boolean(clientId && tenantId);

/** The scope exposed by the app registration's "Expose an API" page. */
export const apiScopes: string[] = [
  import.meta.env.VITE_AZURE_API_SCOPE || `api://${clientId}/access_as_user`,
];

let msal: IPublicClientApplication | null = null;
let redirecting = false;

/** Creates and initializes the MSAL instance; resolves to null when sign-in is not configured. */
export async function initAuth(): Promise<IPublicClientApplication | null> {
  if (!authEnabled) return null;

  msal = await createStandardPublicClientApplication({
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId}`,
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: { cacheLocation: 'localStorage' },
  });
  return msal;
}

/** Sends the browser to the Microsoft sign-in page (once — React strict mode may ask twice). */
export async function startSignIn(): Promise<void> {
  if (!msal || redirecting) return;
  redirecting = true;
  await msal.loginRedirect({ scopes: apiScopes });
}

/**
 * Lets the page fall back to a Microsoft redirect at most twice in two minutes. Without this limit a
 * token that can never be renewed would bounce the browser between the app and Microsoft forever.
 */
function canRedirectAgain(): boolean {
  try {
    const now = Date.now();
    const recent: number[] = (JSON.parse(sessionStorage.getItem('auth.redirects') ?? '[]') as number[])
      .filter((t) => now - t < 120_000);
    if (recent.length >= 2) return false;
    recent.push(now);
    sessionStorage.setItem('auth.redirects', JSON.stringify(recent));
    return true;
  } catch {
    return true; // storage blocked: allow the redirect rather than leave the person stuck
  }
}

/**
 * Returns a bearer token for the API, or null when sign-in is disabled. Renews silently; if Microsoft
 * needs the user to interact again (expired session, consent) the page is redirected to sign in.
 */
export async function getAccessToken(): Promise<string | null> {
  if (!msal) return null;

  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0];
  if (!account) {
    await startSignIn();
    return new Promise<string | null>(() => {}); // the page is navigating away
  }

  try {
    const result = await msal.acquireTokenSilent({ scopes: apiScopes, account });
    return result.accessToken;
  } catch (err) {
    // Either Microsoft wants the person to interact again, or the silent renewal itself failed
    // (a blocked third-party cookie, a hidden frame that timed out...). Both are cured by a normal
    // sign-in redirect; if that keeps failing the error is thrown so the screen can show it.
    if (err instanceof InteractionRequiredAuthError || err instanceof BrowserAuthError) {
      if (!redirecting && canRedirectAgain()) {
        redirecting = true;
        await msal.acquireTokenRedirect({ scopes: apiScopes, account });
        return new Promise<string | null>(() => {});
      }
    }
    throw err;
  }
}

export async function signOut(): Promise<void> {
  if (!msal) return;
  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0];
  await msal.logoutRedirect({ account: account ?? undefined });
}
