import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  mobileEndpoints,
  mobileErrorMessage,
  mobileKeys,
  type MobileApp,
  type MobileDevice,
  type MobilePool,
} from '../../lib/api/endpoints/mobile';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

const PLATFORMS = ['android', 'ios'];
const AUTOMATION_NAMES = ['UiAutomator2', 'XCUITest'];
const INSTALL_POLICIES = ['Preinstalled', 'Install', 'Reinstall'];

const inputClass =
  'w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900';
const labelClass = 'mb-1 block text-sm font-medium text-slate-700';

function statusTone(status: string): 'success' | 'danger' | 'warning' | 'neutral' {
  const normalized = status.toLowerCase();
  if (normalized === 'active' || normalized === 'available') return 'success';
  if (normalized === 'disabled') return 'neutral';
  if (normalized === 'unhealthy' || normalized === 'offline') return 'danger';
  return 'warning';
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

type Tab = 'pools' | 'devices' | 'apps';

/**
 * Mobile device registry (Phase 3 Slice 3C-1/3C-2). Administrative
 * configuration only: pools, devices, and app references. No leasing,
 * sessions, or execution controls exist in this checkpoint. Secrets are
 * never part of this UI — the registry stores no credentials.
 */
export function MobileSettings() {
  const profile = useProfile();
  const queryClient = useQueryClient();
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const canConfigure = hasPermission(profile.data?.permissions, Permissions.SettingsManage);
  const canRead = hasPermission(profile.data?.permissions, Permissions.ExecutionsRead);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [tab, setTab] = useState<Tab>('pools');
  const [formError, setFormError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const [poolName, setPoolName] = useState('');
  const [poolPlatform, setPoolPlatform] = useState('android');
  const [editingPool, setEditingPool] = useState<MobilePool | null>(null);

  const [devicePoolId, setDevicePoolId] = useState('');
  const [devicePlatform, setDevicePlatform] = useState('android');
  const [deviceVersion, setDeviceVersion] = useState('');
  const [deviceManufacturer, setDeviceManufacturer] = useState('');
  const [deviceModel, setDeviceModel] = useState('');
  const [deviceUdid, setDeviceUdid] = useState('');
  const [deviceAutomation, setDeviceAutomation] = useState('UiAutomator2');
  const [editingDevice, setEditingDevice] = useState<MobileDevice | null>(null);

  const [appPlatform, setAppPlatform] = useState('android');
  const [appName, setAppName] = useState('');
  const [appPackage, setAppPackage] = useState('');
  const [appBundle, setAppBundle] = useState('');
  const [appVersion, setAppVersion] = useState('');
  const [appStorageKey, setAppStorageKey] = useState('');
  const [appInstallPolicy, setAppInstallPolicy] = useState('Preinstalled');
  const [appLaunchActivity, setAppLaunchActivity] = useState('');
  const [appDeepLink, setAppDeepLink] = useState('');
  const [editingApp, setEditingApp] = useState<MobileApp | null>(null);

  const projects = useQuery({
    queryKey: ['projects', 'list', '', 1],
    queryFn: () => projectsEndpoints.list('', 1, 100),
    retry: false,
    staleTime: 60_000,
  });

  useEffect(() => {
    if (!projectId && currentProjectId) setProjectId(currentProjectId);
  }, [projectId, currentProjectId]);

  useEffect(() => {
    if (!projectId && !currentProjectId && (projects.data?.items.length ?? 0) > 0) {
      setProjectId(projects.data!.items[0].id);
    }
  }, [projectId, currentProjectId, projects.data]);

  const pools = useQuery({
    queryKey: projectId ? mobileKeys.pools(projectId) : [...mobileKeys.all, 'pools', 'none'],
    queryFn: () => mobileEndpoints.listPools(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const devices = useQuery({
    queryKey: projectId ? mobileKeys.devices(projectId) : [...mobileKeys.all, 'devices', 'none'],
    queryFn: () => mobileEndpoints.listDevices(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const apps = useQuery({
    queryKey: projectId ? mobileKeys.apps(projectId) : [...mobileKeys.all, 'apps', 'none'],
    queryFn: () => mobileEndpoints.listApps(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const invalidate = () => {
    if (!projectId) return;
    void queryClient.invalidateQueries({ queryKey: mobileKeys.pools(projectId) });
    void queryClient.invalidateQueries({ queryKey: mobileKeys.devices(projectId) });
    void queryClient.invalidateQueries({ queryKey: mobileKeys.apps(projectId) });
  };

  const onMutationError = (error: ApiError) => {
    setSaved(null);
    setFormError(mobileErrorMessage(error.status, error.code));
  };

  const savePool = useMutation({
    mutationFn: () =>
      editingPool
        ? mobileEndpoints.updatePool(projectId!, editingPool.id, {
            name: poolName.trim(),
            enabled: editingPool.enabled,
            rowVersion: editingPool.rowVersion,
          })
        : mobileEndpoints.createPool(projectId!, { name: poolName.trim(), platform: poolPlatform }),
    onSuccess: () => {
      setFormError(null);
      setSaved(editingPool ? 'Device pool updated.' : 'Device pool created.');
      setPoolName('');
      setEditingPool(null);
      invalidate();
    },
    onError: onMutationError,
  });

  const togglePool = useMutation({
    mutationFn: (pool: MobilePool) => mobileEndpoints.setPoolEnabled(projectId!, pool.id, !pool.enabled),
    onSuccess: () => invalidate(),
    onError: onMutationError,
  });

  const saveDevice = useMutation({
    mutationFn: () =>
      editingDevice
        ? mobileEndpoints.updateDevice(projectId!, editingDevice.id, {
            platformVersion: deviceVersion.trim() === '' ? null : deviceVersion.trim(),
            manufacturer: deviceManufacturer.trim() === '' ? null : deviceManufacturer.trim(),
            model: deviceModel.trim() === '' ? null : deviceModel.trim(),
            udid: deviceUdid.trim() === '' ? null : deviceUdid.trim(),
            automationName: deviceAutomation,
            enabled: editingDevice.enabled,
            rowVersion: editingDevice.rowVersion,
          })
        : mobileEndpoints.registerDevice(projectId!, {
            poolId: devicePoolId,
            platform: devicePlatform,
            platformVersion: deviceVersion.trim() === '' ? null : deviceVersion.trim(),
            manufacturer: deviceManufacturer.trim() === '' ? null : deviceManufacturer.trim(),
            model: deviceModel.trim() === '' ? null : deviceModel.trim(),
            udid: deviceUdid.trim() === '' ? null : deviceUdid.trim(),
            automationName: deviceAutomation,
          }),
    onSuccess: () => {
      setFormError(null);
      setSaved(editingDevice ? 'Device updated.' : 'Device registered with one default slot.');
      resetDeviceForm();
      invalidate();
    },
    onError: onMutationError,
  });

  const toggleDevice = useMutation({
    mutationFn: (device: MobileDevice) =>
      mobileEndpoints.setDeviceEnabled(projectId!, device.id, !device.enabled),
    onSuccess: () => invalidate(),
    onError: onMutationError,
  });

  const saveApp = useMutation({
    mutationFn: () =>
      editingApp
        ? mobileEndpoints.updateApp(projectId!, editingApp.id, {
            name: appName.trim(),
            packageId: appPackage.trim() === '' ? null : appPackage.trim(),
            bundleId: appBundle.trim() === '' ? null : appBundle.trim(),
            version: appVersion.trim() === '' ? null : appVersion.trim(),
            storageKey: appStorageKey.trim() === '' ? null : appStorageKey.trim(),
            installPolicy: appInstallPolicy,
            launchActivity: appLaunchActivity.trim() === '' ? null : appLaunchActivity.trim(),
            deepLink: appDeepLink.trim() === '' ? null : appDeepLink.trim(),
            rowVersion: editingApp.rowVersion,
          })
        : mobileEndpoints.createApp(projectId!, {
            platform: appPlatform,
            name: appName.trim(),
            packageId: appPackage.trim() === '' ? null : appPackage.trim(),
            bundleId: appBundle.trim() === '' ? null : appBundle.trim(),
            version: appVersion.trim() === '' ? null : appVersion.trim(),
            storageKey: appStorageKey.trim() === '' ? null : appStorageKey.trim(),
            installPolicy: appInstallPolicy,
            launchActivity: appLaunchActivity.trim() === '' ? null : appLaunchActivity.trim(),
            deepLink: appDeepLink.trim() === '' ? null : appDeepLink.trim(),
          }),
    onSuccess: () => {
      setFormError(null);
      setSaved(editingApp ? 'Mobile app updated.' : 'Mobile app registered.');
      resetAppForm();
      invalidate();
    },
    onError: onMutationError,
  });

  const resetDeviceForm = () => {
    setDevicePoolId('');
    setDeviceVersion('');
    setDeviceManufacturer('');
    setDeviceModel('');
    setDeviceUdid('');
    setEditingDevice(null);
  };

  const resetAppForm = () => {
    setAppName('');
    setAppPackage('');
    setAppBundle('');
    setAppVersion('');
    setAppStorageKey('');
    setAppLaunchActivity('');
    setAppDeepLink('');
    setEditingApp(null);
  };

  const startEditPool = (pool: MobilePool) => {
    setEditingPool(pool);
    setPoolName(pool.name);
    setPoolPlatform(pool.platform.toLowerCase());
    setFormError(null);
    setSaved(null);
  };

  const startEditDevice = (device: MobileDevice) => {
    setEditingDevice(device);
    setDevicePoolId(device.poolId);
    setDevicePlatform(device.platform.toLowerCase());
    setDeviceVersion(device.platformVersion ?? '');
    setDeviceManufacturer(device.manufacturer ?? '');
    setDeviceModel(device.model ?? '');
    setDeviceUdid(device.udid ?? '');
    setDeviceAutomation(device.automationName);
    setFormError(null);
    setSaved(null);
  };

  const startEditApp = (app: MobileApp) => {
    setEditingApp(app);
    setAppPlatform(app.platform.toLowerCase());
    setAppName(app.name);
    setAppPackage(app.packageId ?? '');
    setAppBundle(app.bundleId ?? '');
    setAppVersion(app.version ?? '');
    setAppStorageKey(app.storageKey ?? '');
    setAppInstallPolicy(app.installPolicy);
    setAppLaunchActivity(app.launchActivity ?? '');
    setAppDeepLink(app.deepLink ?? '');
    setFormError(null);
    setSaved(null);
  };

  const submitPool = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    if (poolName.trim() === '') {
      setFormError('Pool name is required.');
      return;
    }
    savePool.mutate();
  };

  const submitDevice = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    if (!editingDevice && devicePoolId === '') {
      setFormError('Select the pool this device belongs to.');
      return;
    }
    saveDevice.mutate();
  };

  const submitApp = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    if (appName.trim() === '') {
      setFormError('App name is required.');
      return;
    }
    saveApp.mutate();
  };

  const selectProject = (id: string) => {
    setProjectId(id || null);
    setCurrentProjectId(id || null);
    setFormError(null);
    setSaved(null);
  };

  if (!canRead && !profile.isLoading) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Mobile devices</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-600">
            You do not have permission to view mobile resources for this project.
          </p>
        </CardContent>
      </Card>
    );
  }

  const loading = pools.isLoading || devices.isLoading || apps.isLoading;
  const loadError = pools.isError ? pools.error : devices.isError ? devices.error : apps.error;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Mobile devices</CardTitle>
        <CardDescription>
          Project-scoped registry of device pools, devices, and applications under test.
          Registration only — slot leasing, sessions, and Appium execution arrive in a later
          checkpoint. This section stores no secrets or credentials.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div>
          <label htmlFor="mobile-project" className={labelClass}>
            Project
          </label>
          <select
            id="mobile-project"
            value={projectId ?? ''}
            onChange={(e) => selectProject(e.target.value)}
            className={inputClass}
          >
            <option value="">Select a project</option>
            {(projects.data?.items ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </div>

        {!projectId ? (
          <p className="text-sm text-slate-500">Select a project to manage mobile resources.</p>
        ) : loading ? (
          <div aria-label="Loading mobile registry" className="space-y-2">
            <Skeleton className="h-6 w-48" />
            <Skeleton className="h-24" />
          </div>
        ) : loadError ? (
          <ErrorState error={loadError} onRetry={() => { void pools.refetch(); void devices.refetch(); void apps.refetch(); }} />
        ) : (
          <>
            <div className="flex flex-wrap gap-2" role="tablist" aria-label="Mobile registry">
              {(['pools', 'devices', 'apps'] as Tab[]).map((t) => (
                <Button
                  key={t}
                  type="button"
                  role="tab"
                  aria-selected={tab === t}
                  size="sm"
                  variant={tab === t ? 'primary' : 'secondary'}
                  onClick={() => { setTab(t); setFormError(null); setSaved(null); }}
                >
                  {t === 'pools' ? 'Device pools' : t === 'devices' ? 'Devices' : 'Mobile apps'}
                </Button>
              ))}
            </div>

            {saved && (
              <div role="status" className="rounded-md border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
                {saved}
              </div>
            )}
            {formError && (
              <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {formError}
              </div>
            )}

            {!canConfigure && (
              <p className="text-sm text-slate-600">
                You do not have permission to change mobile resources for this project.
              </p>
            )}

            {tab === 'pools' && (
              <div className="space-y-4">
                <ul className="divide-y divide-slate-200 rounded-md border border-slate-200">
                  {(pools.data ?? []).length === 0 && (
                    <li className="px-3 py-2 text-sm text-slate-500">No device pools registered.</li>
                  )}
                  {(pools.data ?? []).map((pool) => (
                    <li key={pool.id} className="flex flex-wrap items-center gap-2 px-3 py-2 text-sm">
                      <Badge tone={pool.enabled ? 'success' : 'neutral'}>
                        {pool.enabled ? 'Active' : 'Disabled'}
                      </Badge>
                      <span className="font-medium text-slate-800">{pool.name}</span>
                      <span className="text-slate-500">{pool.platform}</span>
                      <span className="text-slate-500">{pool.deviceCount} devices</span>
                      {canConfigure && (
                        <>
                          <Button type="button" size="sm" variant="secondary" onClick={() => startEditPool(pool)}>
                            Edit
                          </Button>
                          <Button
                            type="button"
                            size="sm"
                            variant="secondary"
                            disabled={togglePool.isPending}
                            onClick={() => togglePool.mutate(pool)}
                          >
                            {pool.enabled ? 'Disable' : 'Enable'}
                          </Button>
                        </>
                      )}
                    </li>
                  ))}
                </ul>
                {canConfigure && (
                  <form onSubmit={submitPool} className="space-y-3" noValidate>
                    <div>
                      <label htmlFor="mobile-pool-name" className={labelClass}>
                        Pool name{editingPool ? ' (rename)' : ''}
                      </label>
                      <input
                        id="mobile-pool-name"
                        value={poolName}
                        onChange={(e) => setPoolName(e.target.value)}
                        placeholder="android-smoke"
                        className={inputClass}
                      />
                    </div>
                    {!editingPool && (
                      <div>
                        <label htmlFor="mobile-pool-platform" className={labelClass}>
                          Platform (immutable after creation)
                        </label>
                        <select
                          id="mobile-pool-platform"
                          value={poolPlatform}
                          onChange={(e) => setPoolPlatform(e.target.value)}
                          className={inputClass}
                        >
                          {PLATFORMS.map((p) => (
                            <option key={p} value={p}>{p}</option>
                          ))}
                        </select>
                      </div>
                    )}
                    <div className="flex justify-end gap-2">
                      {editingPool && (
                        <Button type="button" size="sm" variant="secondary" onClick={() => { setEditingPool(null); setPoolName(''); }}>
                          Cancel
                        </Button>
                      )}
                      <Button type="submit" size="sm" disabled={savePool.isPending}>
                        {savePool.isPending ? 'Saving…' : editingPool ? 'Update pool' : 'Create pool'}
                      </Button>
                    </div>
                  </form>
                )}
              </div>
            )}

            {tab === 'devices' && (
              <div className="space-y-4">
                <ul className="divide-y divide-slate-200 rounded-md border border-slate-200">
                  {(devices.data ?? []).length === 0 && (
                    <li className="px-3 py-2 text-sm text-slate-500">No devices registered.</li>
                  )}
                  {(devices.data ?? []).map((device) => (
                    <li key={device.id} className="flex flex-wrap items-center gap-2 px-3 py-2 text-sm">
                      <Badge tone={statusTone(device.status)}>{device.status}</Badge>
                      <span className="font-medium text-slate-800">
                        {[device.manufacturer, device.model].filter(Boolean).join(' ') || 'Device'}
                      </span>
                      <span className="text-slate-500">{device.platform}{device.platformVersion ? ` ${device.platformVersion}` : ''}</span>
                      <span className="text-slate-500">{device.automationName}</span>
                      {device.udid && <span className="font-mono text-xs text-slate-500">{device.udid}</span>}
                      <span className="text-slate-500">{device.poolName} · {device.slotCount} slot{device.slotCount === 1 ? '' : 's'}</span>
                      <span className="text-xs text-slate-400">last seen {formatTime(device.lastSeenAt)}</span>
                      {canConfigure && (
                        <>
                          <Button type="button" size="sm" variant="secondary" onClick={() => startEditDevice(device)}>
                            Edit
                          </Button>
                          <Button
                            type="button"
                            size="sm"
                            variant="secondary"
                            disabled={toggleDevice.isPending}
                            onClick={() => toggleDevice.mutate(device)}
                          >
                            {device.enabled ? 'Disable' : 'Enable'}
                          </Button>
                        </>
                      )}
                    </li>
                  ))}
                </ul>
                {canConfigure && (
                  <form onSubmit={submitDevice} className="space-y-3" noValidate>
                    {!editingDevice && (
                      <div>
                        <label htmlFor="mobile-device-pool" className={labelClass}>
                          Pool
                        </label>
                        <select
                          id="mobile-device-pool"
                          value={devicePoolId}
                          onChange={(e) => {
                            setDevicePoolId(e.target.value);
                            const pool = (pools.data ?? []).find((p) => p.id === e.target.value);
                            if (pool) {
                              setDevicePlatform(pool.platform.toLowerCase());
                              setDeviceAutomation(pool.platform.toLowerCase() === 'ios' ? 'XCUITest' : 'UiAutomator2');
                            }
                          }}
                          className={inputClass}
                        >
                          <option value="">Select a pool</option>
                          {(pools.data ?? []).filter((p) => p.enabled).map((p) => (
                            <option key={p.id} value={p.id}>
                              {p.name} ({p.platform})
                            </option>
                          ))}
                        </select>
                      </div>
                    )}
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <label htmlFor="mobile-device-manufacturer" className={labelClass}>Manufacturer</label>
                        <input id="mobile-device-manufacturer" value={deviceManufacturer} onChange={(e) => setDeviceManufacturer(e.target.value)} placeholder="Google" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-device-model" className={labelClass}>Model</label>
                        <input id="mobile-device-model" value={deviceModel} onChange={(e) => setDeviceModel(e.target.value)} placeholder="Pixel 8" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-device-version" className={labelClass}>OS version</label>
                        <input id="mobile-device-version" value={deviceVersion} onChange={(e) => setDeviceVersion(e.target.value)} placeholder="14" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-device-udid" className={labelClass}>UDID</label>
                        <input id="mobile-device-udid" value={deviceUdid} onChange={(e) => setDeviceUdid(e.target.value)} placeholder="emulator-5554" className={inputClass} />
                      </div>
                    </div>
                    <div>
                      <label htmlFor="mobile-device-automation" className={labelClass}>Automation name</label>
                      <select id="mobile-device-automation" value={deviceAutomation} onChange={(e) => setDeviceAutomation(e.target.value)} className={inputClass}>
                        {AUTOMATION_NAMES.map((a) => (
                          <option key={a} value={a}>{a}</option>
                        ))}
                      </select>
                    </div>
                    <div className="flex justify-end gap-2">
                      {editingDevice && (
                        <Button type="button" size="sm" variant="secondary" onClick={resetDeviceForm}>
                          Cancel
                        </Button>
                      )}
                      <Button type="submit" size="sm" disabled={saveDevice.isPending}>
                        {saveDevice.isPending ? 'Saving…' : editingDevice ? 'Update device' : 'Register device'}
                      </Button>
                    </div>
                  </form>
                )}
              </div>
            )}

            {tab === 'apps' && (
              <div className="space-y-4">
                <ul className="divide-y divide-slate-200 rounded-md border border-slate-200">
                  {(apps.data ?? []).length === 0 && (
                    <li className="px-3 py-2 text-sm text-slate-500">No mobile apps registered.</li>
                  )}
                  {(apps.data ?? []).map((app) => (
                    <li key={app.id} className="flex flex-wrap items-center gap-2 px-3 py-2 text-sm">
                      <Badge tone="info">{app.platform}</Badge>
                      <span className="font-medium text-slate-800">{app.name}</span>
                      <span className="font-mono text-xs text-slate-500">{app.packageId ?? app.bundleId}</span>
                      {app.version && <span className="text-slate-500">v{app.version}</span>}
                      <span className="text-slate-500">{app.installPolicy}</span>
                      <Badge tone={app.hasBinary ? 'success' : 'neutral'}>
                        {app.hasBinary ? 'Binary referenced' : 'Preinstalled'}
                      </Badge>
                      {canConfigure && (
                        <Button type="button" size="sm" variant="secondary" onClick={() => startEditApp(app)}>
                          Edit
                        </Button>
                      )}
                    </li>
                  ))}
                </ul>
                {canConfigure && (
                  <form onSubmit={submitApp} className="space-y-3" noValidate>
                    {!editingApp && (
                      <div>
                        <label htmlFor="mobile-app-platform" className={labelClass}>
                          Platform (immutable after creation)
                        </label>
                        <select id="mobile-app-platform" value={appPlatform} onChange={(e) => setAppPlatform(e.target.value)} className={inputClass}>
                          {PLATFORMS.map((p) => (
                            <option key={p} value={p}>{p}</option>
                          ))}
                        </select>
                      </div>
                    )}
                    <div>
                      <label htmlFor="mobile-app-name" className={labelClass}>App name</label>
                      <input id="mobile-app-name" value={appName} onChange={(e) => setAppName(e.target.value)} placeholder="Shop" className={inputClass} />
                    </div>
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <label htmlFor="mobile-app-package" className={labelClass}>Package ID (Android)</label>
                        <input id="mobile-app-package" value={appPackage} onChange={(e) => setAppPackage(e.target.value)} placeholder="com.example.shop" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-app-bundle" className={labelClass}>Bundle ID (iOS)</label>
                        <input id="mobile-app-bundle" value={appBundle} onChange={(e) => setAppBundle(e.target.value)} placeholder="com.example.shop" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-app-version" className={labelClass}>Version</label>
                        <input id="mobile-app-version" value={appVersion} onChange={(e) => setAppVersion(e.target.value)} placeholder="1.2.3" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-app-policy" className={labelClass}>Install policy</label>
                        <select id="mobile-app-policy" value={appInstallPolicy} onChange={(e) => setAppInstallPolicy(e.target.value)} className={inputClass}>
                          {INSTALL_POLICIES.map((p) => (
                            <option key={p} value={p}>{p}</option>
                          ))}
                        </select>
                      </div>
                    </div>
                    <div>
                      <label htmlFor="mobile-app-storage" className={labelClass}>
                        Storage key (object-storage reference only; empty for preinstalled)
                      </label>
                      <input id="mobile-app-storage" value={appStorageKey} onChange={(e) => setAppStorageKey(e.target.value)} placeholder="mobile-apps/shop.apk" className={inputClass} />
                    </div>
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <label htmlFor="mobile-app-activity" className={labelClass}>Launch activity (Android only)</label>
                        <input id="mobile-app-activity" value={appLaunchActivity} onChange={(e) => setAppLaunchActivity(e.target.value)} placeholder="com.example.MainActivity" className={inputClass} />
                      </div>
                      <div>
                        <label htmlFor="mobile-app-deeplink" className={labelClass}>Deep link</label>
                        <input id="mobile-app-deeplink" value={appDeepLink} onChange={(e) => setAppDeepLink(e.target.value)} placeholder="shop://product/1" className={inputClass} />
                      </div>
                    </div>
                    <div className="flex justify-end gap-2">
                      {editingApp && (
                        <Button type="button" size="sm" variant="secondary" onClick={resetAppForm}>
                          Cancel
                        </Button>
                      )}
                      <Button type="submit" size="sm" disabled={saveApp.isPending}>
                        {saveApp.isPending ? 'Saving…' : editingApp ? 'Update app' : 'Register app'}
                      </Button>
                    </div>
                  </form>
                )}
              </div>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}
