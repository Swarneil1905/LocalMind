export function Brand({ size = 32 }: { size?: number }) {
  return (
    <img
      className="brand-mark"
      src="/brand/localmind-mark.svg"
      alt=""
      width={size}
      height={size}
      draggable={false}
    />
  );
}
