import { CiCdSettings } from './CiCdSettings';
import { MobileSettings } from './MobileSettings';
import { ExecutionGridSettings } from './ExecutionGridSettings';
import { AutoTicketSettings } from './AutoTicketSettings';
import { SelfHealingSettings } from './SelfHealingSettings';

export function SettingsPage() {
  return (
    <div className="space-y-4">
      <AutoTicketSettings />
      <SelfHealingSettings />
      <CiCdSettings />
      <MobileSettings />
      <ExecutionGridSettings />
    </div>
  );
}
