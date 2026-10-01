import { CiCdSettings } from './CiCdSettings';
import { ExecutionGridSettings } from './ExecutionGridSettings';
import { AutoTicketSettings } from './AutoTicketSettings';
import { SelfHealingSettings } from './SelfHealingSettings';

export function SettingsPage() {
  return (
    <div className="space-y-4">
      <AutoTicketSettings />
      <SelfHealingSettings />
      <CiCdSettings />
      <ExecutionGridSettings />
    </div>
  );
}
