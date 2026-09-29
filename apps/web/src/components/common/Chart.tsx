import { useEffect, useRef, useState } from 'react';
import * as echarts from 'echarts';

/**
 * Minimal Apache ECharts wrapper (docs/03: ECharts for analytics).
 * Renders the chart when the runtime supports canvas; otherwise falls back
 * to the textual summary so metrics stay accessible (and jsdom-testable).
 * Every chart must be paired with visible text — never color alone.
 */
export function Chart({
  option,
  label,
  fallback,
  height = 260,
}: {
  option: echarts.EChartsCoreOption;
  label: string;
  fallback: React.ReactNode;
  height?: number;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    setFailed(false);
    const el = ref.current;
    if (!el) return;
    let chart: echarts.ECharts | null = null;
    try {
      chart = echarts.init(el);
      chart.setOption(option);
    } catch {
      setFailed(true);
      return;
    }
    const onResize = () => chart?.resize();
    window.addEventListener('resize', onResize);
    return () => {
      window.removeEventListener('resize', onResize);
      chart?.dispose();
      chart = null;
    };
  }, [option]);

  return (
    <div>
      <div
        ref={ref}
        role="img"
        aria-label={label}
        style={{ height, width: '100%' }}
        className={failed ? 'hidden' : undefined}
      />
      {failed && <div className="py-2">{fallback}</div>}
      <div className="sr-only">{fallback}</div>
    </div>
  );
}
