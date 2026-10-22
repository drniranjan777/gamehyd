import { ActionForm } from '../../../../components/ActionForm';
import { ProductFields } from '../../../../components/ProductFields';
import { createProduct } from '../../../../lib/actions';
import { api, type Schemas } from '../../../../lib/api';

export default async function NewProductPage() {
  const cosmetics = await api<Schemas['CosmeticList']>('/admin/catalog/cosmetics');
  return (
    <>
      <h1>New product (draft)</h1>
      <div className="panel">
        <ActionForm action={createProduct} submit="Create draft">
          <label>
            Key (lowercase, permanent)
            <input name="key" pattern="[a-z0-9_]{3,60}" required />
          </label>
          <ProductFields cosmetics={cosmetics.items} />
        </ActionForm>
      </div>
    </>
  );
}
