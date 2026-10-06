namespace Cotifly.Dominio.Comun;

/// <summary>
/// Resultado de una operación de negocio: un valor si salió bien o un <see cref="clsErrorDominio"/> si no.
/// Se usa para los errores esperados (validación, reglas); las excepciones quedan para los fallos inesperados.
/// </summary>
/// <typeparam name="TValor">Tipo del valor en caso de éxito.</typeparam>
public sealed class clsResultado<TValor>
{
    private readonly TValor? _valor;
    private readonly clsErrorDominio? _error;

    private clsResultado(TValor? valor, clsErrorDominio? error)
    {
        _valor = valor;
        _error = error;
    }

    public bool EsExitoso => _error is null;

    public bool EsFallo => !EsExitoso;

    /// <summary>Valor del resultado. Lanza <see cref="InvalidOperationException"/> si es un fallo.</summary>
    public TValor Valor => EsExitoso
        ? _valor!
        : throw new InvalidOperationException($"No hay valor: el resultado es un fallo ({_error!.Codigo}).");

    /// <summary>Error del resultado. Lanza <see cref="InvalidOperationException"/> si es un éxito.</summary>
    public clsErrorDominio Error => _error
        ?? throw new InvalidOperationException("No hay error: el resultado es exitoso.");

    public static clsResultado<TValor> Exito(TValor valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        return new clsResultado<TValor>(valor, null);
    }

    public static clsResultado<TValor> Fallo(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new clsResultado<TValor>(default, error);
    }

    /// <summary>Ejecuta <paramref name="siExito"/> o <paramref name="siFallo"/> según el caso.</summary>
    public TSalida Coincidir<TSalida>(Func<TValor, TSalida> siExito, Func<clsErrorDominio, TSalida> siFallo)
    {
        ArgumentNullException.ThrowIfNull(siExito);
        ArgumentNullException.ThrowIfNull(siFallo);
        return EsExitoso ? siExito(_valor!) : siFallo(_error!);
    }

    /// <summary>Permite <c>return error;</c> en métodos que devuelven <see cref="clsResultado{TValor}"/>.</summary>
    public static implicit operator clsResultado<TValor>(clsErrorDominio error) => Fallo(error);

    /// <summary>Permite <c>return valor;</c> en métodos que devuelven <see cref="clsResultado{TValor}"/>.</summary>
    public static implicit operator clsResultado<TValor>(TValor valor) => Exito(valor);
}
