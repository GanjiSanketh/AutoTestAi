import { useEffect } from 'react';
import { userManager } from '../../lib/auth/oidcClient';

/** Silent-renew iframe target. Renders nothing by design. */
export function SilentRenewPage() {
  useEffect(() => {
    void userManager.signinSilentCallback().catch(() => {
      // Renewal errors surface via UserManager events in AuthProvider.
    });
  }, []);
  return null;
}
