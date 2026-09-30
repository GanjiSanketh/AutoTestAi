import { ExecutionGridSettings } from './ExecutionGridSettings';
import { AutoTicketSettings } from './AutoTicketSettings';

export function SettingsPage() {
  return (
    <div className="space-y-4">
      <AutoTicketSettings />
      <ExecutionGridSettings />
    </div>
  );
}
