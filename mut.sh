set -e
run() { # name, then sed expressions on files
  dotnet build tests/UiPath.Caching.Tests -c Release -f net10.0 2>&1 | grep -E " error |rror\(s\)" | sort -u | head -3
  tests/UiPath.Caching.Tests/bin/Release/net10.0/UiPath.Caching.Tests.exe -class UiPath.Caching.Tests.SpanKeyWriteTests 2>&1 | grep -E "\[FAIL\]|Total:" | sed 's/^ *//'
}
mutate() { f=$1; pat=$2; sed -i "$pat" $f; git diff --stat -- $f | tail -1; }
echo "### M1 ICache drops capability check"; mutate src/UiPath.Caching.Abstractions/ICache.cs 's/this is ISpanKeyWriteCache spanKeyCache/false \&\& this is ISpanKeyWriteCache spanKeyCache/'; run; git checkout -- .
echo "### M2 ICache<T> drops capability check"; mutate src/UiPath.Caching.Abstractions/ICacheOfT.cs 's/this is ISpanKeyWriteCache<T> spanKeyCache/false \&\& this is ISpanKeyWriteCache<T> spanKeyCache/'; run; git checkout -- .
echo "### M3 IHashCache drops capability check"; mutate src/UiPath.Caching.Abstractions/IHashCache.cs 's/this is ISpanKeyWriteHashCache spanKeyCache/false \&\& this is ISpanKeyWriteHashCache spanKeyCache/'; run; git checkout -- .
echo "### M4 IHashCache<T> drops capability check"; mutate src/UiPath.Caching.Abstractions/IHashCacheOfT.cs 's/this is ISpanKeyWriteHashCache<T> spanKeyCache/false \&\& this is ISpanKeyWriteHashCache<T> spanKeyCache/'; run; git checkout -- .
echo "### M5 Cache<T> always builds the CacheKey"; mutate src/UiPath.Caching/CacheOfT.cs 's/SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)\r\?$/false \&\& SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)/;' ; run; git checkout -- .
