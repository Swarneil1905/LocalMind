export function Brand({ size = 32 }: { size?: number }) {
  return (
    <img
      className="brand-mark"
      src="/brand/localmind-mark-256.png"
      alt=""
      width={size}
      height={size}
      draggable={false}
    />
  );
}
