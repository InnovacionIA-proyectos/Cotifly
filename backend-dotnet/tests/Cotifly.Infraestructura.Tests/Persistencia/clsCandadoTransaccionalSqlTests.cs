using System.Data;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// #125: el candado del folio (<c>sp_getapplock</c>) contra SQL Server real con <c>CandadoSql</c> corto. Otra transacción retiene el
/// candado: se reintenta solo la toma (la operación corre como mucho una vez) y, agotados los intentos, <c>SERVICIO_OCUPADO</c>
/// en lugar de una excepción.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCandadoTransaccionalSqlTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos, configurar: null, configuracion: new Dictionary<string, string?>
{
    ["CandadoSql:EsperaMilisegundos"] = "200",
    ["CandadoSql:Intentos"] = "5",
    ["CandadoSql:PausaBaseMilisegundos"] = "20",
})
{
    [Fact]
    public async Task UnCandadoRetenidoAgotaLosIntentosYRespondeServicioOcupadoSinEjecutarLaOperacion()
    {
        var recurso = RecursoUnico();
        await using var retenedor = await RetenerAsync(recurso);
        var ejecuciones = 0;

        var resultado = await EjecutarAsync(recurso, () => Interlocked.Increment(ref ejecuciones));

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
        ejecuciones.Should().Be(0, "sin candado la operación nunca corre");
    }

    [Fact]
    public async Task SiElCandadoSeLiberaEntreIntentosLaOperacionCorreUnaSolaVez()
    {
        var recurso = RecursoUnico();
        var retenedor = await RetenerAsync(recurso);
        var ejecuciones = 0;

        var ejecucion = EjecutarAsync(recurso, () => Interlocked.Increment(ref ejecuciones));
        await Task.Delay(250, Ct); // más que la espera de un intento (200 ms): el primero ya se agotó
        await retenedor.DisposeAsync();
        var resultado = await ejecucion;

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task UnFalloDeLaOperacionNoSeReintentaYDeshaceLaTransaccion()
    {
        var recurso = RecursoUnico();
        var ejecuciones = 0;
        await using var ambito = NuevoAmbito();
        var candado = ambito.ServiceProvider.GetRequiredService<IClsCandadoTransaccional>();

        var resultado = await candado.EjecutarAsync<int>(recurso, _ =>
        {
            Interlocked.Increment(ref ejecuciones);
            return Task.FromResult<clsResultado<int>>(new clsErrorDominio(clsCodigosError.FolioConsecutivoAgotado, "agotado"));
        }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.FolioConsecutivoAgotado);
        ejecuciones.Should().Be(1);
        (await EjecutarAsync(recurso, () => 0)).EsExitoso.Should().BeTrue("la transacción deshecha liberó el candado");
    }

    [Fact]
    public async Task ElLimiteDelClienteCorreSobreElTimeProviderYNoSobreElRelojDePared()
    {
        // Espera del servidor de 60 s y límite del cliente de 61 s: con el tiempo manual, el límite vence solo cuando la prueba lo
        // adelanta. Antes, CommandTimeout (calculado por SqlClient con DateTime.UtcNow) vencía al instante si el reloj saltaba.
        var tiempo = new clsTiempoManual();
        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad, new Dictionary<string, string?>
        {
            ["CandadoSql:EsperaMilisegundos"] = "60000",
            ["CandadoSql:MargenRespuestaMilisegundos"] = "1000",
            ["CandadoSql:Intentos"] = "1",
        }, configurar: s => s.AddSingleton<TimeProvider>(tiempo));
        var recurso = RecursoUnico();
        await using var retenedor = await RetenerAsync(recurso);
        await using var ambito = proveedor.CreateAsyncScope();
        var candado = ambito.ServiceProvider.GetRequiredService<IClsCandadoTransaccional>();

        var ejecucion = candado.EjecutarAsync<int>(recurso, _ => Task.FromResult(clsResultado<int>.Exito(1)), Ct);
        while (tiempo.Pendientes == 0)
        {
            await Task.Delay(10, Ct);
        }

        ejecucion.IsCompleted.Should().BeFalse("el servidor sigue esperando el candado");
        tiempo.Avanzar(TimeSpan.FromSeconds(61));
        var resultado = await ejecucion.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
    }

    [Theory]
    [InlineData(1, 200, 200, 300)]
    [InlineData(2, 200, 400, 500)]
    [InlineData(3, 200, 800, 900)]
    [InlineData(5, 0, 0, 0)]
    public void LaPausaEntreIntentosCreceExponencialmenteConVariacionAcotada(int intento, int baseMs, int minimo, int maximo)
    {
        var pausa = clsCandadoTransaccionalSql.Pausa(intento, baseMs).TotalMilliseconds;

        pausa.Should().BeInRange(minimo, maximo);
    }

    private static string RecursoUnico() => $"cotifly:prueba-candado:{Guid.NewGuid():N}";

    private async Task<clsResultado<int>> EjecutarAsync(string recurso, Func<int> operacion)
    {
        await using var ambito = NuevoAmbito();
        var candado = ambito.ServiceProvider.GetRequiredService<IClsCandadoTransaccional>();
        return await candado.EjecutarAsync<int>(recurso, _ => Task.FromResult(clsResultado<int>.Exito(operacion())), Ct);
    }

    /// <summary>Otra sesión toma el candado en su transacción y lo retiene hasta disponer el resultado.</summary>
    private async Task<IAsyncDisposable> RetenerAsync(string recurso)
    {
        var conexion = new SqlConnection(BaseDatos.CadenaCotifly);
        await conexion.OpenAsync(Ct);
        var transaccion = (SqlTransaction)await conexion.BeginTransactionAsync(Ct);
        await using var comando = new SqlCommand("sp_getapplock", conexion, transaccion) { CommandType = CommandType.StoredProcedure };
        comando.Parameters.AddWithValue("@Resource", recurso);
        comando.Parameters.AddWithValue("@LockMode", "Exclusive");
        comando.Parameters.AddWithValue("@LockOwner", "Transaction");
        var retorno = comando.Parameters.Add(new SqlParameter("@Retorno", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
        await comando.ExecuteNonQueryAsync(Ct);
        ((int)retorno.Value).Should().BeGreaterThanOrEqualTo(0);
        return new clsRetenedor(conexion, transaccion);
    }

    private sealed class clsRetenedor(SqlConnection conexion, SqlTransaction transaccion) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaccion.RollbackAsync();
            await transaccion.DisposeAsync();
            await conexion.DisposeAsync();
        }
    }
}
