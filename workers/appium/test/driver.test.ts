import { describe, expect, it } from 'vitest';
import {
  WebdriverIoDriver,
  classifyDriverError,
  parseAppiumServerUrl,
  toWebdriverCapabilities,
} from '../src/driver.js';

describe('appium endpoint parsing', () => {
  it('accepts http and https URLs with explicit ports and paths', () => {
    expect(parseAppiumServerUrl('http://localhost:4723')).toEqual({
      protocol: 'http:',
      hostname: 'localhost',
      port: 4723,
      path: '/',
    });
    expect(parseAppiumServerUrl('https://farm.example:8443/wd/hub')).toEqual({
      protocol: 'https:',
      hostname: 'farm.example',
      port: 8443,
      path: '/wd/hub',
    });
  });

  it('applies default ports and rejects non-http schemes', () => {
    expect(parseAppiumServerUrl('http://device-host').port).toBe(80);
    expect(parseAppiumServerUrl('https://device-host').port).toBe(443);
    expect(() => parseAppiumServerUrl('ftp://device-host')).toThrow();
    expect(() => parseAppiumServerUrl('not-a-url')).toThrow();
    expect(() => parseAppiumServerUrl('')).toThrow();
  });
});

describe('driver error classification', () => {
  it('maps connectivity failures to environment', () => {
    expect(classifyDriverError(new Error('fetch failed: ECONNREFUSED')).kind).toBe('environment');
    expect(classifyDriverError(new Error('Session creation timed out')).kind).toBe('environment');
  });

  it('maps capability problems to automation', () => {
    expect(
      classifyDriverError(new Error('invalid argument: capability appPackage missing')).kind,
    ).toBe('automation');
  });

  it('maps device absence to environment', () => {
    expect(classifyDriverError(new Error('device offline: emulator-5554')).kind).toBe('environment');
  });

  it('bounds message length', () => {
    expect(classifyDriverError(new Error('x'.repeat(9000))).message.length).toBeLessThanOrEqual(4000);
  });
});

describe('capability translation', () => {
  it('maps the fixed server schema 1:1 with no added keys', () => {
    const caps = toWebdriverCapabilities({
      platformName: 'Android',
      automationName: 'UiAutomator2',
      deviceName: 'Pixel 8',
      udid: 'emulator-5554',
      appPackage: 'com.example.shop',
      appActivity: 'com.example.shop.MainActivity',
      bundleId: null,
      app: null,
      noReset: true,
      fullReset: false,
      newCommandTimeout: 120,
    });
    expect(caps).toEqual({
      platformName: 'Android',
      'appium:automationName': 'UiAutomator2',
      'appium:deviceName': 'Pixel 8',
      'appium:udid': 'emulator-5554',
      'appium:appPackage': 'com.example.shop',
      'appium:appActivity': 'com.example.shop.MainActivity',
      'appium:noReset': true,
      'appium:fullReset': false,
      'appium:newCommandTimeout': 120,
    });
  });

  it('omits absent optionals', () => {
    const caps = toWebdriverCapabilities({
      platformName: 'Android',
      automationName: 'UiAutomator2',
      deviceName: null,
      udid: null,
      appPackage: null,
      appActivity: null,
      bundleId: null,
      app: null,
      noReset: false,
      fullReset: false,
      newCommandTimeout: 60,
    });
    expect(Object.keys(caps).sort()).toEqual([
      'appium:automationName',
      'appium:fullReset',
      'appium:newCommandTimeout',
      'appium:noReset',
      'platformName',
    ]);
  });
});

describe('driver session handle tracking', () => {
  it('deleteSession is idempotent for unknown ids without a server', async () => {
    const driver = new WebdriverIoDriver();
    await expect(driver.deleteSession('no-such-session')).resolves.toBeUndefined();
    expect(driver.hasSession('no-such-session')).toBe(false);
  });

  it('createSession fails deterministically with environment classification when unreachable', async () => {
    const driver = new WebdriverIoDriver();
    await expect(
      driver.createSession(
        {
          platformName: 'Android',
          automationName: 'UiAutomator2',
          deviceName: null,
          udid: null,
          appPackage: null,
          appActivity: null,
          bundleId: null,
          app: null,
          noReset: true,
          fullReset: false,
          newCommandTimeout: 1,
        },
        {
          endpoint: {
            protocol: 'http:',
            hostname: '127.0.0.1',
            port: 1,
            path: '/',
          },
          newCommandTimeoutMs: 2000,
        },
      ),
    ).rejects.toMatchObject({ kind: 'environment' });
  }, 15000);
});
