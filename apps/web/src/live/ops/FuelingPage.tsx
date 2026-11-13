import { opsApi } from '../api';
import { FuelingDetailPage } from '../fuelings';

/** Ops: one fueling with the records behind it. */
export function FuelingPage() {
  return <FuelingDetailPage portal="ops" backTo="/ops/transactions" load={(id, signal) => opsApi.fuelings.get(id, signal)} />;
}
