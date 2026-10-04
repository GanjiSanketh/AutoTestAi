import { describe, expect, it } from 'vitest';
import { loadMobileConfig } from '../src/config.js';

describe('mobile worker config', () => {
  it('defaults to the appium worker identity without browsers', () => {
    const config = loadMobileConfig();
    expect(config.workerType).toBe('appium');
    expect(config.framework).toBe('appium');
    expect(config.capacity).toBeGreaterThanOrEqual(1);
    expect(config.capacity).toBeLessThanOrEqual(16);
  });

  it('reserves a worker-local Appium endpoint default', () => {
    const config = loadMobileConfig();
    expect(config.appiumServerUrl).toBe('http://localhost:4723');
  });

  it('clamps numeric bounds', () => {
    process.env.WORKER_CAPACITY = '999';
    try {
      expect(loadMobileConfig().capacity).toBe(16);
    } finally {
      delete process.env.WORKER_CAPACITY;
    }
  });

  it('bounds the healing-suggest round trip', () => {
    expect(loadMobileConfig().healingAiTimeoutMs).toBe(15000);
  });
});
