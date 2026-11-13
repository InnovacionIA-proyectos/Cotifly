using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Dominio.Tests.Identidad;

public sealed class clsIdentidadTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly string _hash = new('a', 64);

    [Fact]
    public void UnUsuarioNuevoTieneIdPropioYRegistraSuSesion()
    {
        var usuario = clsUsuarioWeb.Crear(" APEREZ ", "Ana Pérez", "aperez@x.com", "Antioquia", _ahora).Valor;

        usuario.Id.Should().NotBeEmpty();
        usuario.NombreUsuario.Should().Be("APEREZ");
        usuario.UltimaSesion.Should().Be(_ahora);
    }

    [Fact]
    public void ElNombreUsuarioEsObligatorioYAcotadoA255() =>
        new[] { "", new string('u', 256) }
            .Select(n => clsUsuarioWeb.Crear(n, null, null, null, _ahora).EsFallo)
            .Should().AllBeEquivalentTo(true);

    [Fact]
    public void ActualizarDatosNoBorraConValoresVaciosYElTelefonoSiSePuedeBorrar()
    {
        var usuario = clsUsuarioWeb.Crear("APEREZ", "Ana Pérez", "aperez@x.com", "Antioquia", _ahora).Valor;

        usuario.ActualizarDatos(" ", "nuevo@x.com", null);
        usuario.CambiarTelefono(" 3001234567 ");
        usuario.RegistrarSesion(_ahora.AddDays(1));

        usuario.NombreCompleto.Should().Be("Ana Pérez");
        usuario.Email.Should().Be("nuevo@x.com");
        usuario.Regional.Should().Be("Antioquia");
        usuario.Telefono.Should().Be("3001234567");
        usuario.UltimaSesion.Should().Be(_ahora.AddDays(1));

        usuario.CambiarTelefono("");
        usuario.Telefono.Should().BeNull();
    }

    [Fact]
    public void UnTokenDeRefrescoGuardaElHashEnMinusculaYVenceSegunLaVigencia()
    {
        var usuario = Guid.NewGuid();

        var token = clsTokenRefresco.Emitir(usuario, new string('F', 64), _ahora, TimeSpan.FromDays(30)).Valor;

        token.TokenHash.Should().Be(new string('f', 64));
        token.ExpiraAt.Should().Be(_ahora.AddDays(30));
        token.FamiliaId.Should().NotBeEmpty();
        token.EstaVigente(_ahora.AddDays(29)).Should().BeTrue();
        token.EstaVigente(_ahora.AddDays(30)).Should().BeFalse();
    }

    [Theory]
    [InlineData("corto")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ElHashDebeSerSha256Hexadecimal(string hash) =>
        clsTokenRefresco.Emitir(Guid.NewGuid(), hash, _ahora, TimeSpan.FromDays(1)).EsFallo.Should().BeTrue();

    [Fact]
    public void ElTokenExigeUsuarioYVigenciaPositiva()
    {
        clsTokenRefresco.Emitir(Guid.Empty, _hash, _ahora, TimeSpan.FromDays(1)).EsFallo.Should().BeTrue();
        clsTokenRefresco.Emitir(Guid.NewGuid(), _hash, _ahora, TimeSpan.Zero).EsFallo.Should().BeTrue();
    }

    [Fact]
    public void LaRotacionConservaLaFamiliaYDejaElAnteriorSinVigencia()
    {
        var anterior = clsTokenRefresco.Emitir(Guid.NewGuid(), _hash, _ahora, TimeSpan.FromDays(30)).Valor;
        var nuevo = clsTokenRefresco.Emitir(anterior.UsuarioId, new string('b', 64), _ahora, TimeSpan.FromDays(30), anterior.FamiliaId).Valor;

        anterior.MarcarReemplazado(nuevo.Id, _ahora.AddMinutes(1));

        nuevo.FamiliaId.Should().Be(anterior.FamiliaId);
        anterior.ReemplazadoPorId.Should().Be(nuevo.Id);
        anterior.RevocadoAt.Should().Be(_ahora.AddMinutes(1));
        anterior.EstaVigente(_ahora.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void RevocarConservaLaPrimeraFecha()
    {
        var token = clsTokenRefresco.Emitir(Guid.NewGuid(), _hash, _ahora, TimeSpan.FromDays(30)).Valor;

        token.Revocar(_ahora.AddMinutes(1));
        token.Revocar(_ahora.AddMinutes(5));

        token.RevocadoAt.Should().Be(_ahora.AddMinutes(1));
    }

    [Fact]
    public void UnJtiRevocadoSePurgaAlVencer()
    {
        var revocado = clsTokenRevocado.Crear(" jti-1 ", _ahora.AddMinutes(60)).Valor;

        revocado.Jti.Should().Be("jti-1");
        revocado.PuedePurgarse(_ahora).Should().BeFalse();
        revocado.PuedePurgarse(_ahora.AddMinutes(60)).Should().BeTrue();
        clsTokenRevocado.Crear(new string('j', 65), _ahora).EsFallo.Should().BeTrue();
    }

    [Fact]
    public void UnRolInternoNaceActivoYSinRolExternoHastaQueSeMapee()
    {
        var rol = clsRol.Crear(RolInterno.Comercial, " Comercial ").Valor;

        rol.Codigo.Should().Be(RolInterno.Comercial);
        rol.Descripcion.Should().Be("Comercial");
        rol.IdRolExterno.Should().BeNull();
        rol.Activo.Should().BeTrue();

        rol.MapearRolExterno(34);
        rol.Desactivar();

        rol.IdRolExterno.Should().Be(34);
        rol.Activo.Should().BeFalse();
    }

    [Fact]
    public void UnRolSoloAceptaRolesInternosConocidosYDescripcionAcotada()
    {
        clsRol.Crear((RolInterno)9, null).EsFallo.Should().BeTrue();
        clsRol.Crear(RolInterno.GerenteComercial, new string('d', 201)).EsFallo.Should().BeTrue();
    }

    [Fact]
    public void UnRolNaceSinAltaManualYSePuedeMarcarComoSensible()
    {
        clsRol.Crear(RolInterno.Comercial, "Comercial").Valor.RequiereAltaManual.Should().BeFalse();
        clsRol.Crear(RolInterno.Director, "Director comercial", requiereAltaManual: true).Valor.RequiereAltaManual.Should().BeTrue();
    }

    [Fact]
    public void DesactivarYReactivarNoLlevanRevisorYSonIdempotentes()
    {
        var usuario = Guid.NewGuid();
        var asignacion = clsUsuarioRol.Asignar(usuario, 2, _ahora).Valor;

        // Auto-revocar (sistema): sin revisor, con la fecha.
        asignacion.Desactivar(_ahora.AddDays(1));
        asignacion.Activo.Should().BeFalse();
        asignacion.RevisadoPor.Should().BeNull("la auto-revocación del login no tiene revisor humano");
        asignacion.RevisadoAt.Should().Be(_ahora.AddDays(1));

        // Idempotente sobre una fila ya inactiva.
        asignacion.Desactivar(_ahora.AddDays(2));
        asignacion.RevisadoAt.Should().Be(_ahora.AddDays(1), "no vuelve a tocar una fila ya inactiva");

        // Reactivar (sistema): sin revisor, reactiva la fila existente.
        asignacion.Reactivar(_ahora.AddDays(3));
        asignacion.Activo.Should().BeTrue();
        asignacion.RevisadoPor.Should().BeNull();
        asignacion.AsignadoAt.Should().Be(_ahora.AddDays(3));

        // Idempotente sobre una fila ya activa.
        asignacion.Reactivar(_ahora.AddDays(4));
        asignacion.AsignadoAt.Should().Be(_ahora.AddDays(3));
    }

    [Fact]
    public void UnaAsignacionDeRolNaceActivaYAlRetirarlaQuedaLaRevision()
    {
        var usuario = Guid.NewGuid();
        var revisor = Guid.NewGuid();
        var asignacion = clsUsuarioRol.Asignar(usuario, 2, _ahora).Valor;

        asignacion.UsuarioId.Should().Be(usuario);
        asignacion.RolId.Should().Be(2);
        asignacion.AsignadoAt.Should().Be(_ahora);
        asignacion.Activo.Should().BeTrue();
        asignacion.RevisadoPor.Should().BeNull();

        asignacion.Retirar(revisor, _ahora.AddDays(1));

        asignacion.Activo.Should().BeFalse();
        asignacion.RevisadoPor.Should().Be(revisor);
        asignacion.RevisadoAt.Should().Be(_ahora.AddDays(1));
        FluentActions.Invoking(() => asignacion.RegistrarRevision(Guid.Empty, _ahora)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnaAsignacionExigeUsuarioYRol()
    {
        clsUsuarioRol.Asignar(Guid.Empty, 1, _ahora).EsFallo.Should().BeTrue();
        clsUsuarioRol.Asignar(Guid.NewGuid(), 0, _ahora).EsFallo.Should().BeTrue();
    }

    [Fact]
    public void ElVinculoConElAlmacenCorporativoSeFijaUnaVezYNoCambia()
    {
        var sinVinculo = clsUsuarioWeb.Crear("APEREZ", null, null, null, _ahora).Valor;
        var vinculado = clsUsuarioWeb.Crear("LGOMEZ", null, null, null, _ahora, idUsuarioExterno: 102).Valor;

        sinVinculo.IdUsuarioExterno.Should().BeNull();
        sinVinculo.VincularUsuarioExterno(101);
        sinVinculo.VincularUsuarioExterno(101);

        sinVinculo.IdUsuarioExterno.Should().Be(101);
        vinculado.IdUsuarioExterno.Should().Be(102);
        FluentActions.Invoking(() => vinculado.VincularUsuarioExterno(999)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UnEventoDeHerramientaExigeNombreYDuracionNoNegativa()
    {
        clsEventoHerramienta.Registrar("", null, null, true, 1).EsFallo.Should().BeTrue();
        clsEventoHerramienta.Registrar("obtener_margen", null, null, true, -1).EsFallo.Should().BeTrue();

        var evento = clsEventoHerramienta.Registrar(" obtener_margen ", "{}", " ", true, 12, "t").Valor;

        evento.Herramienta.Should().Be("obtener_margen");
        evento.OutputResumen.Should().BeNull();
        evento.Exito.Should().BeTrue();
    }
}
