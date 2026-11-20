import './stage.css';

/** The moving brand background of the stage: three slow glows, still under reduced motion. */
export function StageAurora({ fade = false }: { fade?: boolean }) {
  return (
    <div aria-hidden className="stage-aurora" data-fade={fade ? '' : undefined}>
      <span />
      <span />
      <span />
    </div>
  );
}
