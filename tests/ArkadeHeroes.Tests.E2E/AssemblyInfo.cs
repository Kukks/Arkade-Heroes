// E2E tests share one regtest stack and one faucet wallet — concurrent test
// classes race coin selection (VTXO_ALREADY_SPENT). Run them serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
