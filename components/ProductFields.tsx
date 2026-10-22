import type { Schemas } from '@rwbr/api-contracts';

const local = (iso: string | null | undefined) => (iso ? iso.slice(0, 16) : '');

/** Fields shared by the create and edit forms. The portal edits one grant per product; the API allows up to ten. */
export function ProductFields({
  cosmetics,
  product,
}: {
  cosmetics: Schemas['CosmeticList']['items'];
  product?: Schemas['AdminProduct'];
}) {
  const grant = product?.grants[0];
  return (
    <>
      <label>
        Display name
        <input name="displayName" defaultValue={product?.displayName} maxLength={60} required />
      </label>
      <label>
        Description
        <textarea name="description" defaultValue={product?.description} maxLength={300} />
      </label>
      <div className="row">
        <label>
          Price currency
          <select name="currency" defaultValue={product?.price.currency ?? 'COIN'}>
            <option value="COIN">Coins (earned in game)</option>
            <option value="GEM">Gems</option>
            <option value="INR">INR (Google Play / App Store)</option>
          </select>
        </label>
        <label>
          Price in minor units (coins, gems, or paise)
          <input
            name="amountMinor"
            type="number"
            min={1}
            max={10000000}
            defaultValue={product?.price.amountMinor}
            required
          />
        </label>
      </div>
      <div className="row">
        <label>
          Grants
          <select name="grantType" defaultValue={grant?.type ?? 'COSMETIC'}>
            <option value="COSMETIC">A cosmetic</option>
            <option value="CURRENCY">Gems</option>
          </select>
        </label>
        <label>
          Cosmetic
          <select
            name="cosmeticKey"
            defaultValue={grant?.type === 'COSMETIC' ? grant.cosmeticKey : undefined}
          >
            {cosmetics.map((c) => (
              <option key={c.cosmeticKey} value={c.cosmeticKey}>
                {c.displayName} ({c.kind}, {c.rarity})
              </option>
            ))}
          </select>
        </label>
        <label>
          Gem amount
          <input
            name="gemAmount"
            type="number"
            min={1}
            defaultValue={grant?.type === 'CURRENCY' ? grant.amount : 100}
          />
        </label>
      </div>
      <div className="row">
        <label>
          Google Play product id
          <input
            name="skuAndroid"
            defaultValue={product?.skus.ANDROID}
            pattern="[a-z0-9_.]{3,100}"
          />
        </label>
        <label>
          App Store product id
          <input name="skuIos" defaultValue={product?.skus.IOS} pattern="[a-z0-9_.]{3,100}" />
        </label>
      </div>
      <div className="row">
        <label>
          On sale from (UTC, optional)
          <input
            name="availableFrom"
            type="datetime-local"
            defaultValue={local(product?.availableFrom)}
          />
        </label>
        <label>
          On sale until (UTC, optional)
          <input
            name="availableUntil"
            type="datetime-local"
            defaultValue={local(product?.availableUntil)}
          />
        </label>
      </div>
    </>
  );
}
