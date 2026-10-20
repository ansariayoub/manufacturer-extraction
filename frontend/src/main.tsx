import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import './index.css'
import App from './App.tsx'
import { AuthGate } from './auth/AuthGate.tsx'
import { initAuth } from './auth/auth.ts'

// Microsoft sign-in is only wired in when the Azure app registration is configured (see auth.ts);
// otherwise the app renders directly, exactly as it did before sign-in existed.
initAuth().then((msal) => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      {msal ? (
        <MsalProvider instance={msal}>
          <AuthGate>
            <App />
          </AuthGate>
        </MsalProvider>
      ) : (
        <App />
      )}
    </StrictMode>,
  )
})
