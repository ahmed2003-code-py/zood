import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Sheet } from '@/design-system';
import { stationApi, type Fueling } from '../api';
import { FuelingDetail, FuelingsList } from '../fuelings';

/** Station portal: the fuelings at the operator's stations. A row opens its detail in a sheet. */
export function StationFuelingsPage() {
  const { t } = useTranslation('live');
  const [selected, setSelected] = useState<Fueling>();
  return (
    <>
      <FuelingsList
        portal="station"
        title={t('fuelings.title')}
        description={t('fuelings.stationDescription')}
        load={(query, signal) => stationApi.fuelings(query, signal)}
        onOpen={setSelected}
      />
      <Sheet
        open={Boolean(selected)}
        onOpenChange={(open) => {
          if (!open) setSelected(undefined);
        }}
        title={t('fuelings.detailTitle', { reference: selected?.reference ?? '' })}
      >
        {selected && <FuelingDetail fueling={selected} portal="station" />}
      </Sheet>
    </>
  );
}
