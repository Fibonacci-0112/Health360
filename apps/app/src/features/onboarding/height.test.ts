import { describe, expect, it } from 'vitest';
import { parseHeight } from './height';

describe('parseHeight', () => {
  it('stores feet and inches as total inches with the exact canonical value', () => {
    expect(parseHeight({ ft: '5', in: '10' }, 'ft_in')).toEqual({ height: 70, height_unit: 'in', height_cm: 177.8 });
  });

  it('keeps centimetres as typed, accepting a decimal comma', () => {
    expect(parseHeight({ cm: '172,5' }, 'cm')).toEqual({ height: 172.5, height_unit: 'cm', height_cm: 172.5 });
  });

  it('returns null when nothing was entered', () => {
    expect(parseHeight({}, 'cm')).toBeNull();
    expect(parseHeight({ ft: ' ', in: '' }, 'ft_in')).toBeNull();
  });

  it('rejects implausible values', () => {
    expect(() => parseHeight({ cm: '17' }, 'cm')).toThrow(RangeError);
    expect(() => parseHeight({ ft: '5', in: '12' }, 'ft_in')).toThrow(RangeError);
  });
});
