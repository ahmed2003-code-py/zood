import qrcode from 'qrcode-generator';
import { useMemo } from 'react';
import { cn } from '@/lib/cn';

/** QR code drawn as SVG in the current text color (no raw colors). */
export function QrCode({ value, size = 64, label, className }: { value: string; size?: number; label: string; className?: string }) {
  const cells = useMemo(() => {
    const code = qrcode(0, 'M');
    code.addData(value);
    code.make();
    const count = code.getModuleCount();
    const dark: Array<[number, number]> = [];
    for (let row = 0; row < count; row++) {
      for (let col = 0; col < count; col++) if (code.isDark(row, col)) dark.push([col, row]);
    }
    return { count, dark };
  }, [value]);

  return (
    <svg
      role="img"
      aria-label={label}
      viewBox={`-1 -1 ${cells.count + 2} ${cells.count + 2}`}
      width={size}
      height={size}
      className={cn('shrink-0', className)}
      shapeRendering="crispEdges"
    >
      {cells.dark.map(([x, y]) => (
        <rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} fill="currentColor" />
      ))}
    </svg>
  );
}
