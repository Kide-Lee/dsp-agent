export interface PlanOptions {
  beltType?: number;
  insertType?: number;
  recycleMode?: number;
  rows?: number;
  stackSize?: number;
  floor?: number;
  stationPiler?: number;
}
export function planSingle(itemId: number, rate: number, options?: PlanOptions): string;
