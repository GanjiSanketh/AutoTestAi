import { CiCdSettings } from './CiCdSettings';
import { MobileSettings } from './MobileSettings';
import { VisualBaselines } from './VisualBaselines';
import { ExecutionGridSettings } from './ExecutionGridSettings';
import { AutoTicketSettings } from './AutoTicketSettings';
import { SelfHealingSettings } from './SelfHealingSettings';
import { SlaTargetSettings } from './SlaTargetSettings';

export function SettingsPage() {
  return (
    <div className="space-y-4">
      <AutoTicketSettings />
      <SelfHealingSettings />
      <SlaTargetSettings />
      <CiCdSettings />
      <MobileSettings />
      <VisualBaselines />
      <ExecutionGridSettings />
    </div>
  );
}
