using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    // What the admission rested on is checked when the transaction runs: the binding, the limit, whether the key has its field, and for a new field the room that was seen.
    private static void AddMarkerConditions(ITransaction transaction, RedisKey control, string markerField, MarkerAdmission admission, bool first)
    {
        transaction.AddCondition(admission.BindingFieldValue.IsNull ? Condition.HashNotExists(control, MarkerBinding.BindingFieldName) : Condition.HashEqual(control, MarkerBinding.BindingFieldName, admission.BindingFieldValue));
        transaction.AddCondition(Condition.HashEqual(control, admission.BindingField, admission.Binding));
        transaction.AddCondition(admission.Limit.IsNull ? Condition.HashNotExists(control, "lim") : Condition.HashEqual(control, "lim", admission.Limit));
        transaction.AddCondition(admission.Present ? Condition.HashExists(control, markerField) : Condition.HashNotExists(control, markerField));
        if (first && !admission.Present)
        {
            transaction.AddCondition(Condition.HashLengthLessThan(control, admission.Ceiling));
        }
    }

    // The reads of the fallback of a marked write, in the script's order.
    private async Task<MarkerAdmission> AdmitMarkerAsync(RedisKey control, string markerField, bool first)
    {
        var field = await _db.HashGetAsync(control, MarkerBinding.BindingFieldName, Master).ConfigureAwait(false);
        var limit = await _db.HashGetAsync(control, "lim", Master).ConfigureAwait(false);
        var present = await _db.HashExistsAsync(control, markerField, Master).ConfigureAwait(false);
        var length = await _db.HashLengthAsync(control, Master).ConfigureAwait(false);
        var bindingField = field.IsNull ? MarkerBinding.IdentityField : field.ToString();
        var binding = await _db.HashGetAsync(control, bindingField, Master).ConfigureAwait(false);
        var ceiling = limit.IsNull ? 0 : (long)limit;
        long refusal = 0;
        if (binding.IsNull || binding == string.Empty)
        {
            refusal = NoBinding;
        }
        else if (first && !present && length >= ceiling)
        {
            refusal = RecordFull;
        }

        return new MarkerAdmission(refusal, field, bindingField, binding, limit, present, ceiling);
    }

    /// <summary>What a marked write rests on in the control record: the field that names the binding, its value, the limit, and whether the marker field already exists.</summary>
    /// <param name="Refusal">0 to go on, otherwise <see cref="NoBinding"/> or <see cref="RecordFull"/>, and nothing is changed.</param>
    private readonly record struct MarkerAdmission(long Refusal, RedisValue BindingFieldValue, string BindingField, RedisValue Binding, RedisValue Limit, bool Present, long Ceiling);
}
