import { describe, expect, it } from 'vitest';
import { passwordChecks, validateConfirm, validateEmail, validatePassword, validateUsername } from '../auth/password.js';
import { validateRegistration } from '../pages/auth/RegisterPage.jsx';
import { checkHeader, precheckCsvFile } from '../utils/csv.js';

describe('password policy', () => {
  it('accepts a password meeting every rule', () => {
    expect(validatePassword('Correct-Horse-9')).toBeNull();
    expect(passwordChecks('Correct-Horse-9')).toEqual({ length: true, upper: true, lower: true, digit: true, symbol: true });
  });

  it.each([
    ['', 'Password is required.'],
    ['Sh0rt!', 'Password must be at least 12 characters.'],
    ['a'.repeat(120) + 'A1!aaaaaaaa', 'Password must be at most 128 characters.'],
    ['lowercase-only-1', 'Password must contain an upper-case letter.'],
    ['UPPERCASE-ONLY-1', 'Password must contain a lower-case letter.'],
    ['NoDigitsHere!!', 'Password must contain a digit.'],
    ['NoSymbols12345', 'Password must contain a symbol.'],
  ])('rejects %j', (pw, msg) => {
    expect(validatePassword(pw)).toBe(msg);
  });

  it('treats exactly 12 and 128 characters as valid lengths', () => {
    expect(validatePassword('Abcdefgh12!x')).toBeNull();
    expect(validatePassword('A1!' + 'a'.repeat(125))).toBeNull();
  });

  it('validates confirmation and email', () => {
    expect(validateConfirm('abc', '')).toBe('Please confirm the password.');
    expect(validateConfirm('abc', 'abd')).toBe('Passwords do not match.');
    expect(validateConfirm('abc', 'abc')).toBeNull();
    expect(validateEmail('')).toBe('Email is required.');
    expect(validateEmail('nope')).toBe('Enter a valid email address.');
    expect(validateEmail('pilot@example.com')).toBeNull();
  });

  it('validates the full registration form', () => {
    expect(validateRegistration({ username: 'x.y_z-1', email: 'x@y.io', displayName: 'X', password: 'Correct-Horse-9', confirmPassword: 'Correct-Horse-9' })).toEqual({});
    const errs = validateRegistration({ username: '', email: '', displayName: ' ', password: 'short', confirmPassword: 'other' });
    expect(Object.keys(errs).sort()).toEqual(['confirmPassword', 'displayName', 'email', 'password', 'username']);
  });

  it.each([
    ['ab', 'Username must be at least 3 characters.'],
    ['a'.repeat(65), 'Username must be at most 64 characters.'],
    ['Upper', 'Use only lower-case letters, digits, dots, underscores and hyphens.'],
    ['has space', 'Use only lower-case letters, digits, dots, underscores and hyphens.'],
  ])('rejects username %j', (u, msg) => {
    expect(validateUsername(u)).toBe(msg);
  });

  it('accepts usernames at the 3 and 64 character limits', () => {
    expect(validateUsername('abc')).toBeNull();
    expect(validateUsername('a'.repeat(64))).toBeNull();
  });
});

describe('CSV pre-checks', () => {
  const header = 'aircraft_registration,timestamp_utc,temperature,temperature_unit,pressure,pressure_unit,sensor_id';
  it('accepts a header with all required columns in any case/order', () => {
    expect(checkHeader('﻿Pressure_Unit,AIRCRAFT_REGISTRATION,timestamp_utc,temperature,temperature_unit,pressure\r').ok).toBe(true);
  });
  it('reports missing columns', () => {
    expect(checkHeader('aircraft_registration,timestamp_utc').missing).toEqual(['temperature', 'temperature_unit', 'pressure', 'pressure_unit']);
  });
  it('checks extension and header from the file contents', async () => {
    expect(await precheckCsvFile(new File([header + '\nDEMO-A01,...'], 'ok.csv'))).toEqual([]);
    expect(await precheckCsvFile(new File([header], 'data.txt'))).toContain('The file must have a .csv extension.');
    const bad = await precheckCsvFile(new File(['foo,bar\n1,2'], 'bad.csv'));
    expect(bad[0]).toMatch(/missing required column/);
  });
});
