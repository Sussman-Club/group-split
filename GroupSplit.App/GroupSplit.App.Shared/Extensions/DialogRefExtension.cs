using MudBlazor;
using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.App.Shared.Extensions;

public static class DialogRefExtension
{
    extension(IDialogReference dialog)
    {
        public async Task<T?> GetResult<T>(bool throwConversionError = true) where T : class
        {
            var dialogResult = await dialog.Result;
            if (dialogResult is null || dialogResult.Canceled)
                return null;

            if (dialogResult.Data is T castedResult)
                return castedResult;

            if (throwConversionError)
                throw new InvalidCastException($"Dialog result data could not be casted to type {typeof(T).FullName}.");
            return null;
        }
    }
}
