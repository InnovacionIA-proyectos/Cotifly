using System.Collections.Concurrent;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Comun;

/// <summary>
/// <see cref="IClsAlmacenIdempotencia"/> en la memoria del proceso (singleton): un candado por clave serializa las repeticiones
/// simultáneas y el resultado exitoso queda guardado <see cref="IClsAlmacenIdempotencia.Vigencia"/>.
/// </summary>
/// <remarks>
/// Suficiente con una sola réplica de la API (<c>infra/stack.dotnet.dokploy.yml</c>, <c>replicas: 1</c>). Un reinicio olvida las claves,
/// y entonces la defensa que queda es el <c>If-Match</c> obligatorio (en el mismo folio la segunda petición responde
/// <c>CONFLICTO_CONCURRENCIA</c>). Con varias réplicas hay que pasar a una tabla (pendiente reportado en #100).
/// </remarks>
public sealed class clsAlmacenIdempotenciaEnMemoria(IClsReloj reloj) : IClsAlmacenIdempotencia
{
    private readonly ConcurrentDictionary<string, clsEntrada> _entradas = new(StringComparer.Ordinal);

    /// <summary>Claves recordadas (para pruebas y diagnóstico).</summary>
    public int Cantidad => _entradas.Count;

    public async Task<clsResultado<TValor>> EjecutarUnaVezAsync<TValor>(
        string ambito,
        string clave,
        string huella,
        Func<CancellationToken, Task<clsResultado<TValor>>> accion,
        CancellationToken cancellationToken = default)
        where TValor : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ambito);
        ArgumentException.ThrowIfNullOrWhiteSpace(huella);
        ArgumentNullException.ThrowIfNull(accion);
        if (string.IsNullOrWhiteSpace(clave)
            || clave.Length is < IClsAlmacenIdempotencia.LargoMinimoClave or > IClsAlmacenIdempotencia.LargoMaximoClave)
        {
            return new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                $"Idempotency-Key debe tener entre {IClsAlmacenIdempotencia.LargoMinimoClave} y {IClsAlmacenIdempotencia.LargoMaximoClave} caracteres.");
        }

        Purgar();
        var entrada = _entradas.GetOrAdd($"{ambito}\n{clave}", _ => new clsEntrada());
        await entrada.Candado.WaitAsync(cancellationToken);
        try
        {
            if (entrada.Valor is not null && entrada.Expira > reloj.AhoraUtc)
            {
                return entrada.Huella == huella
                    ? (TValor)entrada.Valor
                    : new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                        "Esta Idempotency-Key ya se usó con otra petición: genera una clave nueva para cada intención.");
            }

            var resultado = await accion(cancellationToken);
            if (resultado.EsExitoso)
            {
                entrada.Huella = huella;
                entrada.Valor = resultado.Valor;
                entrada.Expira = reloj.AhoraUtc.Add(IClsAlmacenIdempotencia.Vigencia);
            }

            return resultado;
        }
        finally
        {
            entrada.Candado.Release();
        }
    }

    /// <summary>Olvida las claves vencidas que nadie está usando.</summary>
    private void Purgar()
    {
        var ahora = reloj.AhoraUtc;
        foreach (var (llave, entrada) in _entradas)
        {
            if (entrada.Valor is not null && entrada.Expira <= ahora && entrada.Candado.CurrentCount == 1)
            {
                _entradas.TryRemove(new KeyValuePair<string, clsEntrada>(llave, entrada));
            }
        }
    }

    private sealed class clsEntrada
    {
        public SemaphoreSlim Candado { get; } = new(1, 1);
        public string? Huella { get; set; }
        public object? Valor { get; set; }
        public DateTimeOffset Expira { get; set; }
    }
}
