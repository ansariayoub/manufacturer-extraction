import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { startSignIn } from './auth';

/**
 * Keeps everything behind Microsoft sign-in: with no signed-in account the browser is sent to the
 * Microsoft login page straight away, and the app only renders once an account exists. Whether that
 * account is actually allowed to use the app is a separate check made by the API (GET /api/me).
 */
export function AuthGate({ children }: { children: ReactNode }) {
  const { instance, accounts, inProgress } = useMsal();

  useEffect(() => {
    if (accounts.length > 0) {
      if (!instance.getActiveAccount()) instance.setActiveAccount(accounts[0]);
    } else if (inProgress === InteractionStatus.None) {
      void startSignIn();
    }
  }, [accounts, inProgress, instance]);

  if (accounts.length === 0) {
    return (
      <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--muted)', fontSize: 15 }}>
        Redirecting to Microsoft sign-in…
      </div>
    );
  }

  return <>{children}</>;
}
